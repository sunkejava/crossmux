#pragma once

#include <cstdint>

#include "activities/Activity.h"

class ContentSyncActivity final : public Activity {
 public:
  ContentSyncActivity(GfxRenderer& renderer, MappedInputManager& mappedInput)
      : Activity("ContentSync", renderer, mappedInput) {}

  void onEnter() override;
  void onExit() override;
  void loop() override;
  void render(RenderLock&&) override;
  bool skipLoopDelay() override { return true; }
  bool preventAutoSleep() override { return state == State::Syncing; }

 private:
  enum class State : uint8_t { WaitingForWifi, Syncing, Complete, NotConfigured, Failed };

  State state = State::WaitingForWifi;
  int downloadedCount = 0;
  int unchangedCount = 0;
  int failedCount = 0;
  bool syncStarted = false;
  bool tearDownWifiOnExit = false;

  void launchWifiSelection();
  void onWifiSelectionComplete(bool connected);
  void performSync();
  bool syncPage(int page, bool& hasMore);
  bool syncItem(const char* id, const char* targetPath, const char* sha256, const char* downloadUrl,
                size_t expectedSize);
};
