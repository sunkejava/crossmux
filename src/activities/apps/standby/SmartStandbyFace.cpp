#include "SmartStandbyFace.h"

#include <Arduino.h>
#include <Bitmap.h>
#include <GfxRenderer.h>
#include <HalStorage.h>
#include <I18n.h>
#include <Logging.h>
#include <esp_wifi.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#include "../airpage/AirPageDeviceId.h"
#include "CrossPointSettings.h"
#include "NetworkStartup.h"
#include "WifiCredentialStore.h"
#include "fontIds.h"
#include "network/HttpDownloader.h"

namespace {

constexpr char kCacheDir[] = "/.crosspoint/smart-standby";
constexpr char kImagePath[] = "/.crosspoint/smart-standby/current.bmp";
constexpr char kPartPath[] = "/.crosspoint/smart-standby/current.bmp.part";
constexpr char kBackupPath[] = "/.crosspoint/smart-standby/current.bmp.bak";
constexpr uint32_t kWifiTimeoutMs = 15000u;
constexpr uint32_t kRetryMs = 10000u;
volatile bool gSmartStandbyPushPending = false;

void onSmartStandbyMessage(char*, uint8_t*, unsigned int) { gSmartStandbyPushPending = true; }

bool reached(const uint32_t now, const uint32_t target) { return static_cast<int32_t>(now - target) >= 0; }

}  // namespace

void SmartStandbyFace::onEnter() {
  Storage.ensureDirectoryExists(kCacheDir);
  imageReady_ = validateImage(kImagePath);
  redrawPending_ = true;
  gSmartStandbyPushPending = true;  // Fetch once even if the retained MQTT message is unavailable.
  const long configuredPort = strtol(SETTINGS.smartStandbyMqttPort, nullptr, 10);
  const uint16_t mqttPort = configuredPort > 0 && configuredPort <= UINT16_MAX ? configuredPort : 1883;
  mqtt_.setServer(SETTINGS.smartStandbyMqttHost, mqttPort);
  mqtt_.setCallback(&onSmartStandbyMessage);
  mqtt_.setSocketTimeout(5);
  mqtt_.setKeepAlive(57);
  if (WiFi.status() == WL_CONNECTED) {
    NetworkStartup::prepare(renderer_);
    ownsWifi_ = true;
  }
  state_ = WiFi.status() == WL_CONNECTED ? State::BrokerConnecting : State::Offline;
  stateStartedMs_ = millis();
  nextRetryMs_ = stateStartedMs_;
}

void SmartStandbyFace::onExit() {
  mqtt_.setCallback(nullptr);
  stopNetwork();
  gSmartStandbyPushPending = false;
}

bool SmartStandbyFace::startWifi() {
  if (WIFI_STORE.getCredentialCount() == 0) WIFI_STORE.loadFromFile();
  const std::string lastSsid = WIFI_STORE.getLastConnectedSsid();
  const auto credential = WIFI_STORE.findCredential(lastSsid);
  if (!credential) {
    LOG_ERR("SMARTSTBY", "No saved WiFi credential");
    return false;
  }
  WiFi.persistent(false);
  NetworkStartup::setMode(renderer_, WIFI_STA);
  WiFi.disconnect(true, true);
  delay(100);
  if (credential->password.empty())
    WiFi.begin(credential->ssid.c_str());
  else
    WiFi.begin(credential->ssid.c_str(), credential->password.c_str());
  ownsWifi_ = true;
  state_ = State::WifiConnecting;
  stateStartedMs_ = millis();
  return true;
}

bool SmartStandbyFace::connectBroker() {
  char clientId[40];
  snprintf(clientId, sizeof(clientId), "standby-%s", airpage::deviceId().c_str());
  if (!mqtt_.connect(clientId)) {
    LOG_ERR("SMARTSTBY", "MQTT connect failed state=%d", mqtt_.state());
    return false;
  }
  char topic[96];
  snprintf(topic, sizeof(topic), "crossmux/device/%s/standby/v1", airpage::deviceId().c_str());
  if (!mqtt_.subscribe(topic, 1)) {
    mqtt_.disconnect();
    return false;
  }
  LOG_INF("SMARTSTBY", "MQTT online: %s", topic);
  return true;
}

bool SmartStandbyFace::validateImage(const char* path) const {
  HalFile file;
  if (!Storage.openFileForRead("SMARTSTBY", path, file)) return false;
  Bitmap bitmap(file, false);
  return bitmap.parseHeaders() == BmpReaderError::Ok && bitmap.getWidth() > 0 && bitmap.getHeight() > 0;
}

bool SmartStandbyFace::downloadImage() {
  if (SETTINGS.contentSyncServerUrl[0] == '\0') return false;
  char url[256];
  const size_t length = strnlen(SETTINGS.contentSyncServerUrl, sizeof(SETTINGS.contentSyncServerUrl));
  const char* separator = length > 0 && SETTINGS.contentSyncServerUrl[length - 1] == '/' ? "" : "/";
  const int written = snprintf(url, sizeof(url), "%s%sapi/v1/standby/%s/image.bmp", SETTINGS.contentSyncServerUrl,
                               separator, airpage::deviceId().c_str());
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(url)) return false;
  Storage.remove(kPartPath);
  if (HttpDownloader::downloadToFile(std::string(url), kPartPath) != HttpDownloader::OK || !validateImage(kPartPath)) {
    Storage.remove(kPartPath);
    return false;
  }
  Storage.remove(kBackupPath);
  const bool hadCurrent = Storage.exists(kImagePath);
  if ((hadCurrent && !Storage.rename(kImagePath, kBackupPath)) || !Storage.rename(kPartPath, kImagePath)) {
    if (hadCurrent && !Storage.exists(kImagePath)) Storage.rename(kBackupPath, kImagePath);
    Storage.remove(kPartPath);
    return false;
  }
  Storage.remove(kBackupPath);
  imageReady_ = true;
  redrawPending_ = true;
  return true;
}

void SmartStandbyFace::scheduleRetry() {
  if (mqtt_.connected()) mqtt_.disconnect();
  state_ = State::Backoff;
  nextRetryMs_ = millis() + kRetryMs;
}

void SmartStandbyFace::stopNetwork() {
  if (mqtt_.connected()) mqtt_.disconnect();
  if (ownsWifi_) {
    WiFi.disconnect(false);
    delay(100);
    WiFi.mode(WIFI_OFF);
    esp_wifi_deinit();
  }
  ownsWifi_ = false;
  state_ = State::Offline;
}

bool SmartStandbyFace::tick() {
  const uint32_t now = millis();
  switch (state_) {
    case State::Offline:
      if (!reached(now, nextRetryMs_)) break;
      if (WiFi.status() == WL_CONNECTED) {
        NetworkStartup::prepare(renderer_);
        state_ = State::BrokerConnecting;
      } else if (!startWifi()) {
        scheduleRetry();
      }
      break;
    case State::WifiConnecting:
      if (WiFi.status() == WL_CONNECTED) {
        NetworkStartup::prepare(renderer_);
        state_ = State::BrokerConnecting;
      } else if (now - stateStartedMs_ >= kWifiTimeoutMs) {
        scheduleRetry();
      }
      break;
    case State::BrokerConnecting:
      if (connectBroker())
        state_ = State::Online;
      else
        scheduleRetry();
      break;
    case State::Online:
      if (!mqtt_.connected()) {
        scheduleRetry();
        break;
      }
      mqtt_.loop();
      if (gSmartStandbyPushPending && reached(now, nextRetryMs_)) {
        gSmartStandbyPushPending = false;
        if (!downloadImage()) {
          gSmartStandbyPushPending = true;
          nextRetryMs_ = now + kRetryMs;
        }
      }
      break;
    case State::Backoff:
      if (reached(now, nextRetryMs_)) state_ = State::Offline;
      break;
  }
  const bool redraw = redrawPending_;
  redrawPending_ = false;
  return redraw;
}

void SmartStandbyFace::render(GfxRenderer& renderer, const Rect& viewport) {
  if (!imageReady_) {
    renderer.drawCenteredText(UI_12_FONT_ID, viewport.y + viewport.height / 2 - 30, tr(STR_SMART_STANDBY_EMPTY), true);
    renderer.drawCenteredText(UI_10_FONT_ID, viewport.y + viewport.height / 2 + 10, airpage::deviceId().c_str(), true);
    return;
  }
  HalFile file;
  if (!Storage.openFileForRead("SMARTSTBY", kImagePath, file)) return;
  Bitmap bitmap(file, false);
  if (bitmap.parseHeaders() != BmpReaderError::Ok) return;
  renderer.drawBitmap(bitmap, viewport.x, viewport.y, viewport.width, viewport.height, 0, 0);
}
