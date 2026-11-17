package app.atrium.backupservice

import android.content.Intent
import android.os.Build
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition

/** Starts, updates and stops the backup's foreground service from JavaScript. */
class BackupServiceModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("AtriumBackupService")

    /** Shows or updates the notification; the first call starts the service. Only allowed while the app is in front (Android 12+). */
    Function("show") { title: String, text: String, done: Int, total: Int ->
      val context = appContext.reactContext ?: return@Function false
      if (BackupService.running) {
        BackupService.update(context, title, text, done, total)
        return@Function true
      }

      val intent = Intent(context, BackupService::class.java)
        .putExtra(BackupService.EXTRA_TITLE, title)
        .putExtra(BackupService.EXTRA_TEXT, text)
        .putExtra(BackupService.EXTRA_DONE, done)
        .putExtra(BackupService.EXTRA_TOTAL, total)
      try {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
          context.startForegroundService(intent)
        } else {
          context.startService(intent)
        }
        true
      } catch (e: Exception) {
        // Not allowed from the background, or over the day's limit: the run goes on without it.
        false
      }
    }

    Function("hide") {
      val context = appContext.reactContext ?: return@Function Unit
      try {
        context.startService(Intent(context, BackupService::class.java).setAction(BackupService.ACTION_STOP))
      } catch (e: Exception) {
        // Not running, or the app is no longer allowed to start it: Android removes it with the process.
      }
      Unit
    }
  }
}
