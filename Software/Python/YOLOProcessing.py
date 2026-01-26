import cv2
import socket
import threading
import time
import numpy as np
from ultralytics import YOLO

# --- CONFIGURATION ---
SYSTEM_CONFIG = [
    {
        "id": 0,
        "name": "Camera Node 0",
        "cam_url": "http://192.168.5.176:81/stream",
        "motor_ip": "192.168.5.183",
        "motor_port": 3333
    },
    {
        "id": 1,
        "name": "Camera Node 1",
        "cam_url": "http://192.168.5.187:81/stream", # Change to your 2nd ESP32-CAM IP
        "motor_ip": "192.168.5.172",               # Change to your 2nd Motor ESP32 IP
        "motor_port": 3333
    }
]

# --- TUNING SETTINGS ---
YOLO_MODEL_PATH = r"C:\Users\Alienware R7\Documents\PlatformIO\Projects\camESP32\runs\detect\train6\weights\best.pt"
YOLO_SIZE = 320
CONFIDENCE = 0.4
LOOKAHEAD_TIME = 0.4
PREDICTION_TIMEOUT = 0.5 # Stop tracking if object lost for 500ms

# DISPLAY SETTINGS
DISPLAY_WIDTH = 640
DISPLAY_HEIGHT = 480
# UI Area at the bottom for the button
BUTTON_AREA_HEIGHT = 60 
BTN_W = 140
BTN_H = 40
BTN_X = (DISPLAY_WIDTH - BTN_W) // 2
BTN_Y = (BUTTON_AREA_HEIGHT - BTN_H) // 2

# PERFORMANCE SETTINGS
# 0.050 = Run YOLO every 50ms (approx 20 FPS for detection)
# Faster than before, but still throttled to prevent UI freezes.
INFERENCE_INTERVAL = 0.050 

class KalmanTracker:
    def __init__(self):
        # State: [x, y, vx, vy]
        self.kalman = cv2.KalmanFilter(4, 2)
        self.kalman.measurementMatrix = np.array([[1,0,0,0],
                                                 [0,1,0,0]], np.float32)
        self.kalman.transitionMatrix = np.array([[1,0,1,0],
                                                [0,1,0,1],
                                                [0,0,1,0],
                                                [0,0,0,1]], np.float32)
        # Reduced process noise to make the prediction smoother during "skips"
        self.kalman.processNoiseCov = np.eye(4, dtype=np.float32) * 0.01
        self.kalman.measurementNoiseCov = np.eye(2, dtype=np.float32) * 1.0
        
        self.last_time = time.time()
        self.found = False

    def predict_only(self):
        """
        Runs the physics prediction step. 
        Must be called EVERY frame.
        """
        current_time = time.time()
        dt = current_time - self.last_time
        self.last_time = current_time
        
        # Update physics with time passed
        self.kalman.transitionMatrix[0, 2] = dt
        self.kalman.transitionMatrix[1, 3] = dt
        
        # Calculate new predicted state (statePre)
        self.kalman.predict()
        
        # CRITICAL: If we don't correct this frame (YOLO skip or miss), 
        # we must trust the physics for now. Update statePost to match prediction.
        # This allows the drone to 'coast' smoothly.
        self.kalman.statePost = self.kalman.statePre.copy()

    def correct(self, x, y):
        """
        Updates the filter with a real measurement from YOLO.
        """
        measurement = np.array([[np.float32(x)], [np.float32(y)]])
        self.kalman.correct(measurement)
        self.found = True

    def stop_prediction(self):
        """
        Resets tracking state.
        Stops the drone from 'predicting' movement when the target is lost.
        """
        # 1. Zero out velocity
        self.kalman.statePost[2] = 0
        self.kalman.statePost[3] = 0
        
        # 2. Mark as lost. This forces get_prediction to return CENTER.
        self.found = False

    def get_prediction(self, lookahead=0.0):
        if not self.found:
            # Return CENTER (320, 240) if not found. 
            # This makes the UDP loop send (0,0), stopping the motors.
            return 320, 240 
            
        state = self.kalman.statePost
        x, y = state[0][0], state[1][0]
        vx, vy = state[2][0], state[3][0]
        
        pred_x = x + (vx * lookahead)
        pred_y = y + (vy * lookahead)
        return pred_x, pred_y

class VideoStream:
    def __init__(self, src=0):
        self.src = src
        self.stream = None 
        self.stopped = False
        self.frame = None
        self.lock = threading.Lock()
        self.connected = False

    def start(self):
        t = threading.Thread(target=self.update, daemon=True)
        t.start()
        return self
        
    def force_reconnect(self):
        """Manually triggers a reconnect attempt."""
        with self.lock:
            print(f"Manual reconnect triggered for {self.src}")
            if self.stream:
                self.stream.release()
            self.stream = None
            self.connected = False

    def update(self):
        while not self.stopped:
            if self.stream is None or not self.stream.isOpened():
                self.connected = False
                try:
                    self.stream = cv2.VideoCapture(self.src)
                    # Low buffer size is critical for low latency
                    self.stream.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                    if not self.stream.isOpened():
                        time.sleep(2.0)
                        continue
                    else:
                        print(f"Connected to {self.src}")
                        self.connected = True
                except Exception as e:
                    print(f"Stream error {self.src}: {e}")
                    time.sleep(2.0)
                    continue
            
            (grabbed, frame) = self.stream.read()
            
            if grabbed:
                with self.lock:
                    self.frame = frame
            else:
                self.stream.release()
                self.stream = None
                self.connected = False
                time.sleep(0.1)

    def read(self):
        with self.lock:
            return self.frame.copy() if self.frame is not None else None

    def stop(self):
        self.stopped = True
        if self.stream:
            self.stream.release()

class DroneController:
    def __init__(self, config, sock):
        self.config = config
        self.sock = sock
        self.name = config['name']
        
        self.stream = VideoStream(config['cam_url']).start()
        self.tracker = KalmanTracker()
        
        self.running = True
        self.udp_thread = threading.Thread(target=self._udp_sender_loop, daemon=True)
        self.udp_thread.start()
        
        self.display_frame = np.zeros((DISPLAY_HEIGHT, DISPLAY_WIDTH, 3), dtype=np.uint8)
        
        # Time tracking
        self.last_inference_time = 0
        self.last_detection_time = 0

    def _udp_sender_loop(self):
        target_ip = self.config['motor_ip']
        target_port = self.config['motor_port']
        
        while self.running:
            # We get the prediction continuously
            px, py = self.tracker.get_prediction(lookahead=LOOKAHEAD_TIME)
            
            # If tracker.found is False, px/py will be 320/240 (Center).
            # This makes norm_x/norm_y = 0.0, which stops the motors.
            norm_x = (px - 320.0) / 320.0
            norm_y = (240.0 - py) / 240.0
            norm_x = max(-1.0, min(1.0, norm_x))
            norm_y = max(-1.0, min(1.0, norm_y))
            
            msg = f"{norm_x:.3f},{norm_y:.3f}"
            try:
                self.sock.sendto(msg.encode(), (target_ip, target_port))
            except Exception:
                pass 
                
            time.sleep(0.016) # ~60Hz motor updates

    def process(self, model):
        raw_frame = self.stream.read()
        
        if raw_frame is None:
            # Create placeholder if no frame
            frame = np.zeros((DISPLAY_HEIGHT, DISPLAY_WIDTH, 3), dtype=np.uint8)
            status = "Connecting..." if not self.stream.connected else "No Signal"
            cv2.putText(frame, f"{self.name}: {status}", (50, 240), cv2.FONT_HERSHEY_SIMPLEX, 1, (0,0,255), 2)
        else:
            # Basic Pre-processing (Fast)
            frame = cv2.flip(raw_frame, 0)
            frame = cv2.resize(frame, (DISPLAY_WIDTH, DISPLAY_HEIGHT))
            
            current_time = time.time()
            
            # --- THE OPTIMIZATION: INFERENCE THROTTLING ---
            # 1. Always update the Kalman physics (Predict step)
            self.tracker.predict_only()
            
            # 2. Check if it's time to run YOLO
            run_yolo = (current_time - self.last_inference_time) > INFERENCE_INTERVAL
            
            if run_yolo:
                # HEAVY TASK: Run Inference
                results = model.predict(frame, conf=CONFIDENCE, verbose=False, imgsz=YOLO_SIZE)
                self.last_inference_time = current_time
                
                # If detected, update Kalman with measurement (Correct step)
                if results[0].boxes:
                    best_box = max(results[0].boxes, key=lambda x: x.conf[0])
                    x, y, w, h = best_box.xywh[0].cpu().numpy()
                    self.tracker.correct(x, y)
                    self.last_detection_time = current_time
            
            # 3. Time Cutoff Logic
            # If we haven't seen an object in X seconds, stop tracking.
            if (current_time - self.last_detection_time) > PREDICTION_TIMEOUT:
                self.tracker.stop_prediction()

            # 4. Visualization
            px, py = self.tracker.get_prediction(0)
            fx, fy = self.tracker.get_prediction(LOOKAHEAD_TIME)
            
            cv2.circle(frame, (int(px), int(py)), 5, (0, 255, 0), -1) # Green = Current est.
            cv2.circle(frame, (int(fx), int(fy)), 5, (0, 0, 255), -1) # Red = Future est.
            
            # Clean Status Overlay
            cv2.putText(frame, self.name, (10, 30), 
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2)
        
        # --- UI LAYER (Button) ---
        ui_strip = np.zeros((BUTTON_AREA_HEIGHT, DISPLAY_WIDTH, 3), dtype=np.uint8)
        # Background slightly gray
        ui_strip[:] = (50, 50, 50)
        
        # Draw Connect Button
        btn_color = (0, 180, 0) # Dark Green
        cv2.rectangle(ui_strip, (BTN_X, BTN_Y), (BTN_X + BTN_W, BTN_Y + BTN_H), btn_color, -1)
        
        # Button Text
        text_size = cv2.getTextSize("CONNECT", cv2.FONT_HERSHEY_SIMPLEX, 0.6, 2)[0]
        text_x = BTN_X + (BTN_W - text_size[0]) // 2
        text_y = BTN_Y + (BTN_H + text_size[1]) // 2
        cv2.putText(ui_strip, "CONNECT", (text_x, text_y), 
                   cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
                   
        # Combine Frame and UI
        full_frame = np.vstack((frame, ui_strip))
        return full_frame

    def handle_click(self, x, y):
        """
        Handles mouse clicks. 
        x, y are relative to this drone's specific column in the main window.
        """
        # Check if click is in the bottom UI area
        if y >= DISPLAY_HEIGHT:
            # Adjust y to be relative to the UI strip
            ui_y = y - DISPLAY_HEIGHT
            
            # Check collision with button
            if (BTN_X <= x <= BTN_X + BTN_W) and (BTN_Y <= ui_y <= BTN_Y + BTN_H):
                self.stream.force_reconnect()

    def stop(self):
        self.running = False
        self.stream.stop()

# --- MOUSE CALLBACK ---
def mouse_callback(event, x, y, flags, param):
    if event == cv2.EVENT_LBUTTONDOWN:
        controllers = param
        # Determine which column (drone) was clicked
        # Each drone column is DISPLAY_WIDTH wide
        col_index = x // DISPLAY_WIDTH
        
        if 0 <= col_index < len(controllers):
            # Calculate x relative to that specific column
            rel_x = x % DISPLAY_WIDTH
            controllers[col_index].handle_click(rel_x, y)

def main():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print("Loading YOLO model...")
    model = YOLO(YOLO_MODEL_PATH)
    
    controllers = []
    for config in SYSTEM_CONFIG:
        d = DroneController(config, sock)
        controllers.append(d)
        
    print("System Armed. Press 'q' to quit.")
    
    # Create Window and Register Mouse Callback
    window_name = "Multi-Drone Tracking (Optimized)"
    cv2.namedWindow(window_name)
    cv2.setMouseCallback(window_name, mouse_callback, controllers)
    
    try:
        while True:
            display_images = []
            
            # This loop can now run very fast because process() 
            # usually skips the heavy YOLO work.
            for drone in controllers:
                img = drone.process(model)
                display_images.append(img)
            
            if len(display_images) > 0:
                combined_view = np.hstack(display_images)
                cv2.imshow(window_name, combined_view)
            
            if cv2.waitKey(1) == ord('q'):
                break
                
    except KeyboardInterrupt:
        pass
    finally:
        print("Shutting down...")
        for drone in controllers:
            drone.stop()
        cv2.destroyAllWindows()

if __name__ == "__main__":
    main()