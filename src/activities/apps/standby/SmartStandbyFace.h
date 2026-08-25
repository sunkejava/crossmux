#pragma once

#include <PubSubClient.h>
#include <WiFi.h>

#include <cstdint>

#include "StandbyFace.h"

class SmartStandbyFace final : public StandbyFace {
 public:
  explicit SmartStandbyFace(GfxRenderer& renderer) : renderer_(renderer), mqtt_(mqttNet_) {}

  void onEnter() override;
  void onExit() override;
  bool tick() override;
  void render(GfxRenderer& renderer, const Rect& viewport) override;
  StrId titleId() const override { return STR_SMART_STANDBY; }
  uint32_t secondsUntilNextWake() const override { return 60; }
  bool wantsGrayscale() const override { return true; }

 private:
  enum class State : uint8_t { Offline, WifiConnecting, BrokerConnecting, Online, Backoff };

  bool startWifi();
  bool connectBroker();
  bool downloadImage();
  bool validateImage(const char* path) const;
  void scheduleRetry();
  void stopNetwork();

  WiFiClient mqttNet_;
  GfxRenderer& renderer_;
  PubSubClient mqtt_;
  State state_ = State::Offline;
  uint32_t stateStartedMs_ = 0;
  uint32_t nextRetryMs_ = 0;
  bool imageReady_ = false;
  bool redrawPending_ = false;
  bool ownsWifi_ = false;
};
