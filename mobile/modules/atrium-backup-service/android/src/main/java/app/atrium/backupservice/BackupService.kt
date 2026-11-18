package app.atrium.backupservice

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

/**
 * A foreground service that only holds a notification while camera backup
 * runs: it keeps Android from stopping the app's process, so the backup
 * written in JavaScript goes on to its end with the screen off or another
 * app in front. It does no work of its own and stops when told, or when
 * Android's time limit for data transfers runs out (Android 15+).
 */
class BackupService : Service() {
  override fun onBind(intent: Intent?): IBinder? = null

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    if (intent?.action == ACTION_STOP) {
      stopForegroundAndSelf()
      return START_NOT_STICKY
    }

    running = true
    val notification = build(
      this,
      intent?.getStringExtra(EXTRA_TITLE) ?: "",
      intent?.getStringExtra(EXTRA_TEXT) ?: "",
      intent?.getIntExtra(EXTRA_DONE, 0) ?: 0,
      intent?.getIntExtra(EXTRA_TOTAL, 0) ?: 0
    )
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
      startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
    } else {
      startForeground(NOTIFICATION_ID, notification)
    }
    // Not restarted by Android after it was stopped: the next run starts from the app.
    return START_NOT_STICKY
  }

  override fun onTimeout(startId: Int, fgsType: Int) {
    stopForegroundAndSelf()
  }

  override fun onDestroy() {
    running = false
    super.onDestroy()
  }

  private fun stopForegroundAndSelf() {
    running = false
    stopForeground(STOP_FOREGROUND_REMOVE)
    stopSelf()
  }

  companion object {
    const val CHANNEL_ID = "atrium-backup"
    const val NOTIFICATION_ID = 4711
    const val ACTION_STOP = "app.atrium.backupservice.STOP"
    const val EXTRA_TITLE = "title"
    const val EXTRA_TEXT = "text"
    const val EXTRA_DONE = "done"
    const val EXTRA_TOTAL = "total"

    /** Whether the service holds its notification now, in this process. */
    @Volatile
    var running = false

    /** Changes the shown notification in place; no new start, which Android refuses from the background. */
    fun update(context: Context, title: String, text: String, done: Int, total: Int) {
      val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
      manager.notify(NOTIFICATION_ID, build(context, title, text, done, total))
    }

    fun build(context: Context, title: String, text: String, done: Int, total: Int): Notification {
      val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        manager.createNotificationChannel(
          NotificationChannel(CHANNEL_ID, title.ifEmpty { "Backup" }, NotificationManager.IMPORTANCE_LOW)
        )
      }

      val open = context.packageManager.getLaunchIntentForPackage(context.packageName)?.let {
        PendingIntent.getActivity(context, 0, it, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
      }
      val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        Notification.Builder(context, CHANNEL_ID)
      } else {
        @Suppress("DEPRECATION")
        Notification.Builder(context)
      }
      builder
        .setSmallIcon(context.applicationInfo.icon)
        .setContentTitle(title)
        .setContentText(text)
        .setOngoing(true)
        .setOnlyAlertOnce(true)
      if (open != null) {
        builder.setContentIntent(open)
      }
      if (total > 0) {
        builder.setProgress(total, done.coerceIn(0, total), false)
      }
      return builder.build()
    }
  }
}
