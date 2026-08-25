#include "ContentSyncActivity.h"

#include <ArduinoJson.h>
#include <GfxRenderer.h>
#include <HalStorage.h>
#include <I18n.h>
#include <Logging.h>
#include <Memory.h>
#include <WiFi.h>
#include <mbedtls/sha256.h>

#include <cstdio>
#include <cstring>
#include <string>

#include "CrossPointSettings.h"
#include "MappedInputManager.h"
#include "NetworkStartup.h"
#include "SilentRestart.h"
#include "activities/network/WifiSelectionActivity.h"
#include "components/SubpageLayout.h"
#include "components/UITheme.h"
#include "fontIds.h"
#include "network/HttpDownloader.h"
#include "util/BookCacheUtils.h"

namespace {
constexpr const char* MANIFEST_PATH = "/.crosspoint/sync-manifest.tmp";
constexpr const char* MARKER_ROOT = "/.crosspoint/file-sync";
constexpr size_t MAX_MANIFEST_BYTES = 32 * 1024;
constexpr size_t HASH_BUFFER_BYTES = 1024;
constexpr int PAGE_SIZE = 10;
constexpr int MAX_PAGES = 100;

bool isHexSha256(const char* value) {
  if (!value || strlen(value) != 64) return false;
  for (size_t i = 0; i < 64; ++i) {
    const char c = value[i];
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
  }
  return true;
}

bool isHexId(const char* value) {
  if (!value || strlen(value) != 24) return false;
  for (size_t i = 0; i < 24; ++i) {
    const char c = value[i];
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
  }
  return true;
}

bool isSafeTargetPath(const char* path) {
  if (!path || (strncmp(path, "/Books/", 7) != 0 && strncmp(path, "/Images/", 8) != 0)) return false;
  const size_t length = strlen(path);
  return length > 8 && length < 192 && strstr(path, "..") == nullptr && strchr(path, '\\') == nullptr &&
         strchr(path, '?') == nullptr && strchr(path, '#') == nullptr;
}

bool hashFile(const char* path, char output[65]) {
  HalFile file;
  if (!Storage.openFileForRead("FILESYNC", path, file)) return false;
  auto buffer = makeUniqueNoThrow<uint8_t[]>(HASH_BUFFER_BYTES);
  if (!buffer) {
    LOG_ERR("FILESYNC", "OOM: hash buffer (%u bytes)", static_cast<unsigned>(HASH_BUFFER_BYTES));
    return false;
  }
  mbedtls_sha256_context context;
  mbedtls_sha256_init(&context);
  bool ok = mbedtls_sha256_starts(&context, 0) == 0;
  while (ok) {
    const int count = file.read(buffer.get(), HASH_BUFFER_BYTES);
    if (count < 0) {
      ok = false;
      break;
    }
    if (count == 0) break;
    ok = mbedtls_sha256_update(&context, buffer.get(), static_cast<size_t>(count)) == 0;
  }
  uint8_t digest[32];
  ok = ok && mbedtls_sha256_finish(&context, digest) == 0;
  mbedtls_sha256_free(&context);
  if (!ok) return false;
  for (size_t i = 0; i < sizeof(digest); ++i) snprintf(output + i * 2, 3, "%02x", digest[i]);
  output[64] = '\0';
  return true;
}

void markerPath(char* output, const size_t outputSize, const char* id) {
  snprintf(output, outputSize, "%s/%s", MARKER_ROOT, id);
}
}  // namespace

void ContentSyncActivity::onEnter() {
  Activity::onEnter();
  if (SETTINGS.contentSyncServerUrl[0] == '\0') {
    state = State::NotConfigured;
    requestUpdate();
    return;
  }
  if (WiFi.status() == WL_CONNECTED) {
    NetworkStartup::prepare(renderer);
    state = State::Syncing;
    requestUpdate();
    return;
  }
  tearDownWifiOnExit = true;
  launchWifiSelection();
}

void ContentSyncActivity::onExit() {
  Activity::onExit();
  Storage.remove(MANIFEST_PATH);
  if (tearDownWifiOnExit && WiFi.getMode() != WIFI_MODE_NULL) {
    WiFi.disconnect(false);
    delay(30);
    silentRestart();
  }
}

void ContentSyncActivity::launchWifiSelection() {
  auto activity = makeUniqueNoThrow<WifiSelectionActivity>(renderer, mappedInput);
  if (!activity) {
    LOG_ERR("FILESYNC", "OOM: WifiSelectionActivity (%u bytes)", static_cast<unsigned>(sizeof(WifiSelectionActivity)));
    state = State::Failed;
    requestUpdate();
    return;
  }
  startActivityForResult(std::move(activity),
                         [this](const ActivityResult& result) { onWifiSelectionComplete(!result.isCancelled); });
}

void ContentSyncActivity::onWifiSelectionComplete(const bool connected) {
  if (!connected) {
    finish();
    return;
  }
  state = State::Syncing;
  requestUpdate();
}

void ContentSyncActivity::loop() {
  if (state == State::Syncing && !syncStarted) {
    syncStarted = true;
    requestUpdateAndWait();
    performSync();
    return;
  }
  if (state != State::Syncing && mappedInput.wasReleased(MappedInputManager::Button::Back)) finish();
}

void ContentSyncActivity::performSync() {
  if (WiFi.status() != WL_CONNECTED || !Storage.ensureDirectoryExists("/.crosspoint") ||
      !Storage.ensureDirectoryExists(MARKER_ROOT) || !Storage.ensureDirectoryExists("/Books") ||
      !Storage.ensureDirectoryExists("/Images")) {
    state = State::Failed;
    requestUpdate();
    return;
  }

  bool hasMore = true;
  int page = 1;
  while (hasMore && page <= MAX_PAGES) {
    if (!syncPage(page, hasMore)) {
      state = State::Failed;
      requestUpdate();
      return;
    }
    ++page;
  }
  if (hasMore) {
    LOG_ERR("FILESYNC", "Manifest exceeded %d pages", MAX_PAGES);
    state = State::Failed;
  } else {
    state = State::Complete;
  }
  requestUpdate();
}

bool ContentSyncActivity::syncPage(const int page, bool& hasMore) {
  char url[256];
  const size_t baseLength = strnlen(SETTINGS.contentSyncServerUrl, sizeof(SETTINGS.contentSyncServerUrl));
  const char* separator = baseLength > 0 && SETTINGS.contentSyncServerUrl[baseLength - 1] == '/' ? "" : "/";
  const int written = snprintf(url, sizeof(url), "%s%sapi/v1/sync/manifest?page=%d&pageSize=%d",
                               SETTINGS.contentSyncServerUrl, separator, page, PAGE_SIZE);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(url) ||
      HttpDownloader::downloadToFile(url, MANIFEST_PATH) != HttpDownloader::OK) {
    LOG_ERR("FILESYNC", "Failed to download manifest page %d", page);
    return false;
  }

  JsonDocument document;
  DeserializationError error;
  {
    HalFile manifest;
    if (!Storage.openFileForRead("FILESYNC", MANIFEST_PATH, manifest) || manifest.fileSize() > MAX_MANIFEST_BYTES)
      return false;
    error = deserializeJson(document, manifest);
  }
  Storage.remove(MANIFEST_PATH);
  if (error || (document["version"] | 0) != 1) {
    LOG_ERR("FILESYNC", "Invalid manifest page %d: %s", page, error.c_str());
    return false;
  }

  const JsonArray items = document["items"].as<JsonArray>();
  if (items.isNull() || items.size() > PAGE_SIZE) return false;
  for (JsonObject item : items) {
    const char* id = item["id"] | "";
    const char* targetPath = item["targetPath"] | "";
    const char* sha256 = item["sha256"] | "";
    const char* downloadUrl = item["downloadUrl"] | "";
    const size_t expectedSize = item["size"] | 0;
    if (!isHexId(id) || !isSafeTargetPath(targetPath) || !isHexSha256(sha256) ||
        (strncmp(downloadUrl, "http://", 7) != 0 && strncmp(downloadUrl, "https://", 8) != 0) || expectedSize == 0) {
      LOG_ERR("FILESYNC", "Rejected invalid manifest item");
      ++failedCount;
      continue;
    }
    if (!syncItem(id, targetPath, sha256, downloadUrl, expectedSize)) ++failedCount;
    requestUpdateAndWait();
  }
  hasMore = document["hasMore"] | false;
  return true;
}

bool ContentSyncActivity::syncItem(const char* id, const char* targetPath, const char* sha256, const char* downloadUrl,
                                   const size_t expectedSize) {
  char marker[80];
  char markerValue[65];
  markerPath(marker, sizeof(marker), id);
  const size_t markerBytes = Storage.readFileToBuffer(marker, markerValue, sizeof(markerValue));
  if (markerBytes == 64 && strcmp(markerValue, sha256) == 0 && Storage.exists(targetPath)) {
    ++unchangedCount;
    return true;
  }

  char part[208];
  char backup[208];
  snprintf(part, sizeof(part), "%s.part", targetPath);
  snprintf(backup, sizeof(backup), "%s.bak", targetPath);
  Storage.remove(part);
  if (HttpDownloader::downloadToFile(downloadUrl, part) != HttpDownloader::OK) return false;

  HalFile downloaded;
  if (!Storage.openFileForRead("FILESYNC", part, downloaded) || downloaded.fileSize64() != expectedSize) {
    Storage.remove(part);
    return false;
  }
  downloaded.close();
  char actualHash[65];
  if (!hashFile(part, actualHash) || strcmp(actualHash, sha256) != 0) {
    LOG_ERR("FILESYNC", "SHA-256 mismatch for %s", targetPath);
    Storage.remove(part);
    return false;
  }

  Storage.remove(backup);
  const bool hadExisting = Storage.exists(targetPath);
  if ((hadExisting && !Storage.rename(targetPath, backup)) || !Storage.rename(part, targetPath)) {
    if (hadExisting && !Storage.exists(targetPath)) Storage.rename(backup, targetPath);
    Storage.remove(part);
    return false;
  }
  Storage.remove(backup);
  if (strstr(targetPath, ".epub") != nullptr) clearBookCache(std::string(targetPath));
  if (!Storage.writeFile(marker, String(sha256))) {
    LOG_ERR("FILESYNC", "Failed to write marker for %s", targetPath);
  }
  ++downloadedCount;
  return true;
}

void ContentSyncActivity::render(RenderLock&&) {
  renderer.clearScreen();
  const auto& metrics = GUI.getMetrics();
  const Rect safeArea = GUI.getScreenSafeArea(renderer, true, false);
  GUI.drawHeader(renderer, Rect{safeArea.x, safeArea.y + metrics.topPadding, safeArea.width, metrics.headerHeight},
                 tr(STR_FILE_SYNC));
  const Rect content = SubpageLayout::contentRect(safeArea, metrics);
  const Rect bounds = SubpageLayout::insetHorizontal(content, metrics.contentSidePadding);
  const char* title = tr(STR_LOADING);
  char detail[96] = {0};
  if (state == State::Complete) {
    title = tr(STR_FILE_SYNC_COMPLETE);
    snprintf(detail, sizeof(detail), tr(STR_FILE_SYNC_RESULT), downloadedCount, unchangedCount, failedCount);
  } else if (state == State::NotConfigured) {
    title = tr(STR_FILE_SYNC_NOT_CONFIGURED);
  } else if (state == State::Failed) {
    title = tr(STR_SYNC_FAILED_MSG);
    snprintf(detail, sizeof(detail), tr(STR_FILE_SYNC_RESULT), downloadedCount, unchangedCount, failedCount);
  } else if (state == State::Syncing) {
    snprintf(detail, sizeof(detail), tr(STR_FILE_SYNC_RESULT), downloadedCount, unchangedCount, failedCount);
  }
  const int titleHeight = renderer.getLineHeight(UI_12_FONT_ID);
  const int detailHeight = detail[0] ? renderer.getLineHeight(UI_10_FONT_ID) : 0;
  const int gap = detail[0] ? SubpageLayout::relatedGap(metrics) : 0;
  const int top = SubpageLayout::centeredTop(content, titleHeight + gap + detailHeight);
  UITheme::drawCenteredText(renderer, bounds, UI_12_FONT_ID, top, title, true, EpdFontFamily::BOLD);
  if (detail[0]) UITheme::drawCenteredText(renderer, bounds, UI_10_FONT_ID, top + titleHeight + gap, detail);
  if (state != State::Syncing && state != State::WaitingForWifi) {
    const auto labels = mappedInput.mapLabels(tr(STR_BACK), "", "", "");
    GUI.drawButtonHints(renderer, labels.btn1, labels.btn2, labels.btn3, labels.btn4);
  }
  renderer.displayBuffer();
}
