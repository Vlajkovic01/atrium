package app.atrium.mediacapabilities

import android.content.Context
import android.hardware.display.DisplayManager
import android.media.MediaCodecList
import android.os.Build
import android.view.Display
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition

/**
 * What this phone decodes and shows, read from Android itself (DESIGN.md
 * 7.2, D-067): every decoder with the profiles, levels and largest picture
 * it takes, and the HDR formats of the screen. The values are Android's own
 * constants; the shared package turns them into the server's terms, where
 * they are tested.
 */
class MediaCapabilitiesModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("AtriumMediaCapabilities")

    Function("read") {
      mapOf(
        "decoders" to decoders(),
        "hdrTypes" to hdrTypes(),
        "sdk" to Build.VERSION.SDK_INT
      )
    }
  }

  private fun decoders(): List<Map<String, Any>> {
    val result = mutableListOf<Map<String, Any>>()
    for (info in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos) {
      if (info.isEncoder) {
        continue
      }

      for (type in info.supportedTypes) {
        val capabilities = try {
          info.getCapabilitiesForType(type)
        } catch (e: IllegalArgumentException) {
          continue
        }

        val entry = mutableMapOf<String, Any>(
          "mime" to type.lowercase(),
          "profileLevels" to capabilities.profileLevels.map { listOf(it.profile, it.level) }
        )
        capabilities.videoCapabilities?.let {
          entry["maxWidth"] = it.supportedWidths.upper
          entry["maxHeight"] = it.supportedHeights.upper
        }
        capabilities.audioCapabilities?.let {
          entry["maxChannels"] = it.maxInputChannelCount
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
          entry["hardware"] = info.isHardwareAccelerated
        }
        result.add(entry)
      }
    }

    return result
  }

  /** Display.HdrCapabilities types: 1 Dolby Vision, 2 HDR10, 3 HLG, 4 HDR10+. */
  private fun hdrTypes(): List<Int> {
    val context = appContext.reactContext ?: return emptyList()
    val display = (context.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager)
      .getDisplay(Display.DEFAULT_DISPLAY) ?: return emptyList()
    return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
      display.mode.supportedHdrTypes.toList()
    } else {
      @Suppress("DEPRECATION")
      display.hdrCapabilities?.supportedHdrTypes?.toList() ?: emptyList()
    }
  }
}
