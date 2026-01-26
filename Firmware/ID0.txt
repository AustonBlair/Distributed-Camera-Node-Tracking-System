#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <Stepper.h>
#include <Wire.h>
#include <Adafruit_MPU6050.h>
#include <Adafruit_Sensor.h>
#include <TinyGPS++.h>
#include <QMC5883LCompass.h> // NEW LIBRARY
#include "soc/soc.h"
#include "soc/rtc_cntl_reg.h"

// --- WIFI CONFIG ---
const char* ssid = "timahuz";
const char* password = "colfax2021";

// --- SENSOR HARDWARE CONFIG ---
#define GPS_RX_PIN 16
#define GPS_TX_PIN 17

Adafruit_MPU6050 mpu;
QMC5883LCompass compass; // NEW OBJECT
TinyGPSPlus gps;
HardwareSerial GPS_Serial(2);

// --- MOTOR PINS ---
// UP DOWN (TILT)
constexpr int S2_IN1 = 13;
constexpr int S2_IN2 = 18;
constexpr int S2_IN3 = 19;
constexpr int S2_IN4 = 5;

// LEFT RIGHT (PAN)
constexpr int S1_IN1 = 25;
constexpr int S1_IN2 = 26;
constexpr int S1_IN3 = 27;
constexpr int S1_IN4 = 14;

const int stepsPerRevolution = 2048;

// --- OBJECTS ---
Stepper panStepper(stepsPerRevolution, S1_IN1, S1_IN3, S1_IN2, S1_IN4);
Stepper tiltStepper(stepsPerRevolution, S2_IN1, S2_IN3, S2_IN2, S2_IN4);
WiFiUDP udp;
const int localPort = 3333;
char packetBuffer[255];

// --- SENSOR VARIABLES ---
unsigned long last_sensor_time = 0;
unsigned long last_udp_time = 0;

// --- NEW CALIBRATION DATA (From your tested snippet) ---
const float mx_off = -402.500f;
const float my_off =  225.000f;
const float mz_off = -101.000f;

const float mx_scale = 1.021568f;
const float my_scale = 0.959860f;
const float mz_scale = 1.021144f;

// --- NEW FILTER SETTINGS ---
const float ALPHA = 0.15; 
float smoothed_heading = 0;

// --- GLOBAL OUTPUT VARS (Used by UDP/System) ---
float pitch_filt = 0.0f;
float heading_filt = 0.0f;

// --- MOTOR TUNING & TRACKING ---
// Tuning updated for normalized input (-1.0 to 1.0)
float Kp = 15.0; // Max speed (15rpm) reached at error of 1.0
float Kd = 5.0;
float DEADBAND_X = 0.1;
float DEADBAND_Y = 0.1;

float currentErrorX = 0, previousErrorX = 0;
float currentErrorY = 0, previousErrorY = 0;
unsigned long lastPacketTime = 0;
unsigned long lastPIDTime = 0;
const int MAX_RPM = 17;
const int MIN_RPM = 5;
const int STEPS_PER_LOOP = 2;

// --- STATE MACHINE VARIABLES ---
bool isActive = false;          // Default: DISABLED
bool isSearching = false;       // 'S' = true, 'T' = false
unsigned long zeroErrorStartTime = 0; // Timer for how long we receive 0,0
const int SEARCH_RPM = 10;      // Speed when searching
const unsigned long SEARCH_DELAY = 500; // Time (ms) at 0,0 before searching starts

// ---------- HELPERS ----------
void handleMotor(Stepper &motor, float pidOutput) {
  if (pidOutput == 0) return;
  int speedRPM = abs(pidOutput);
  speedRPM = constrain(speedRPM, MIN_RPM, MAX_RPM);
  motor.setSpeed(speedRPM);
  int direction = (pidOutput > 0) ? -1 : 1;
  motor.step(direction * STEPS_PER_LOOP);
}

void setup() {
  WRITE_PERI_REG(RTC_CNTL_BROWN_OUT_REG, 0);
  Serial.begin(115200);
  Wire.begin(21, 22);

  // WiFi Init
  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) { delay(500); Serial.print("."); }
  Serial.println("\nWiFi Connected.");
  Serial.print("IP Address: ");
  Serial.println(WiFi.localIP());
  udp.begin(localPort);

  GPS_Serial.begin(9600, SERIAL_8N1, GPS_RX_PIN, GPS_TX_PIN);

  // --- MPU6050 Init (Updated settings) ---
  Serial.println("Initializing MPU6050...");
  if (!mpu.begin()) {
    Serial.println("MPU6050 not found!");
    while (1) delay(10);
  }
  // Changed to match your tested code (2G, 21Hz)
  mpu.setAccelerometerRange(MPU6050_RANGE_2_G);
  mpu.setFilterBandwidth(MPU6050_BAND_21_HZ); 

  // --- QMC5883L Init (New Library) ---
  Serial.println("Initializing QMC5883L...");
  compass.init();

  last_sensor_time = micros();
  last_udp_time = millis();
  Serial.println("System Ready. Mode: DISABLED (Waiting for 'active' command)");
}

void loop() {
  // 1. Read GPS Data
  while (GPS_Serial.available() > 0) {
    gps.encode(GPS_Serial.read());
  }

  // --- 2. SENSOR READING & CALCULATIONS (REPLACED WITH YOUR TESTED CODE) ---
  
  // A. READ ACCELEROMETER
  sensors_event_t a, g, temp;
  mpu.getEvent(&a, &g, &temp);

  // B. READ MAGNETOMETER
  compass.read();
  float raw_mx = compass.getX();
  float raw_my = compass.getY();
  float raw_mz = compass.getZ();

  // C. APPLY CALIBRATION
  float cal_mx = (raw_mx - mx_off) * mx_scale;
  float cal_my = (raw_my - my_off) * my_scale;
  float cal_mz = (raw_mz - mz_off) * mz_scale;

  // D. REMAP AXES TO LOGICAL FRAME (Forward/Right/Down)
  // ACCELEROMETER MAPPING (User: X=Down, Y=Right -> Z=Back) -> Want X=Forward
  float ax_log = -a.acceleration.z; // Physical Z is Back, so -Z is Forward
  float ay_log =  a.acceleration.y; // Physical Y is Right
  float az_log =  a.acceleration.x; // Physical X is Down

  // MAGNETOMETER MAPPING (User: X=Right, Y=Up -> Z=Back) -> Want X=Forward
  float mx_log = -cal_mz;           // INVERTED: Physical Z is Back, so -Z is Forward
  float my_log =  cal_mx;           // Physical X is Right
  float mz_log = -cal_my;           // Physical Y is Up, so -Y is Down

  // E. CALCULATE PITCH AND ROLL (Radians)
  float roll  = atan2(ay_log, az_log);
  float pitch = atan2(-ax_log, sqrt(ay_log * ay_log + az_log * az_log));

  // F. TILT COMPENSATION
  float sinRoll = sin(roll);
  float cosRoll = cos(roll);
  float sinPitch = sin(pitch);
  float cosPitch = cos(pitch);

  // Standard Rotation Matrix for yaw (heading) extraction
  float X_horizontal_full = mx_log * cosPitch + my_log * sinRoll * sinPitch + mz_log * cosRoll * sinPitch;
  float Y_horizontal = my_log * cosRoll - mz_log * sinRoll;

  // G. CALCULATE HEADING
  float heading_rad = atan2(Y_horizontal, X_horizontal_full);
  float heading_deg = heading_rad * 180.0 / PI;

  // H. CORRECTIONS
  heading_deg += 180.0; // Fix 180 degree phase shift

  // Correct for 0-360 wrapping
  if (heading_deg < 0) heading_deg += 360;
  if (heading_deg >= 360) heading_deg -= 360;

  // I. LOW PASS FILTER (From your tested code)
  float diff = heading_deg - smoothed_heading;
  if (diff < -180) diff += 360;
  else if (diff > 180) diff -= 360;
  smoothed_heading += diff * ALPHA;
  
  if (smoothed_heading < 0) smoothed_heading += 360;
  if (smoothed_heading >= 360) smoothed_heading -= 360;

  // --- UPDATE SYSTEM VARIABLES FOR UDP & TRACKING ---
  // Store the results in the variables the rest of the code expects
  heading_filt = smoothed_heading;
  pitch_filt = pitch * 180.0f / PI; // Convert radians to degrees for display

  // Debug Print (Optional, matching your snippet)
  // Serial.print("P:"); Serial.print(pitch_filt, 0);
  // Serial.print(" R:"); Serial.print(roll * 180/PI, 0);
  // Serial.print(" H:"); Serial.println(heading_filt, 1);

  // --- 4. UDP Incoming Control Data ---
  int packetSize = udp.parsePacket();
  if (packetSize) {
    int len = udp.read(packetBuffer, 255);
    if (len > 0) packetBuffer[len] = 0;
    String msg = String(packetBuffer);
    msg.trim(); 

    // --- CHECK FOR MODE COMMANDS FIRST ---
    if (msg.equalsIgnoreCase("active")) {
        isActive = true;
        Serial.println("Mode set to ACTIVE");
        zeroErrorStartTime = millis();
    } 
    else if (msg.equalsIgnoreCase("disabled")) {
        isActive = false;
        Serial.println("Mode set to DISABLED");
        currentErrorX = 0;
        currentErrorY = 0;
    }
    else if (isActive) {
        // --- ONLY PROCESS COORDINATES IF ACTIVE ---
        int commaIndex = msg.indexOf(',');
        if (commaIndex > 0) {
            float inX = msg.substring(0, commaIndex).toFloat();
            float inY = msg.substring(commaIndex + 1).toFloat();

            // Check for (0,0) -> "Center/No Object"
            if (abs(inX) < 0.01 && abs(inY) < 0.01) {
                if (zeroErrorStartTime == 0) {
                    zeroErrorStartTime = millis();
                } 
                else if (millis() - zeroErrorStartTime > SEARCH_DELAY) {
                    isSearching = true; // Enter Searching Mode
                }
            } else {
                zeroErrorStartTime = 0;
                isSearching = false; // Immediately enter Tracking Mode
            }

            currentErrorX = inX;
            currentErrorY = inY;
            lastPacketTime = millis();
        }
    }
  }

  // Safety timeout
  if (millis() - lastPacketTime > 200) {
    currentErrorX = 0;
    currentErrorY = 0;
  }

  // --- DATA TRANSMISSION ---
  if (millis() - last_udp_time > 100) {
    last_udp_time = millis();
    char unityBuffer[180];
    
    char statusChar;
    if (!isActive) {
        statusChar = 'D';
    } else {
        statusChar = isSearching ? 'S' : 'T';
    }

    // Using the NEW pitch_filt and heading_filt calculated above
    sprintf(unityBuffer, "ID:0,M:%c,P:%.1f,MAG:%.1f,LAT:%.6f,LON:%.6f",
            statusChar, pitch_filt, heading_filt, gps.location.lat(), gps.location.lng());
    udp.beginPacket("192.168.5.184", 4444);
    udp.print(unityBuffer);
    udp.endPacket();
  }

  // 5. Motor Control
  unsigned long now_motor = millis();
  if (now_motor - lastPIDTime > 10) {
    float dt_motor = (now_motor - lastPIDTime) / 1000.0f;
    lastPIDTime = now_motor;

    if (isActive) {
        if (isSearching) {
            // --- SEARCHING MODE ---
            panStepper.setSpeed(SEARCH_RPM);
            panStepper.step(1 * STEPS_PER_LOOP); 
        } 
        else {
            // --- TRACKING MODE (PID) ---
            float errorToUseX = (abs(currentErrorX) < DEADBAND_X) ? 0 : currentErrorX;
            float errorToUseY = (abs(currentErrorY) < DEADBAND_Y) ? 0 : currentErrorY;

            float outputX = 0;
            if (errorToUseX != 0) {
                float errorRateX = (errorToUseX - previousErrorX) / dt_motor;
                outputX = (Kp * errorToUseX) + (Kd * errorRateX);
            }
            previousErrorX = errorToUseX;

            float outputY = 0;
            if (errorToUseY != 0) {
                float errorRateY = (errorToUseY - previousErrorY) / dt_motor;
                outputY = (Kp * errorToUseY) + (Kd * errorRateY);
            }
            previousErrorY = errorToUseY;

            handleMotor(panStepper, outputX);
            handleMotor(tiltStepper, -outputY);
        }
    }
  }
}