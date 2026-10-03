package android.app

import android.content.Context
import android.os.Bundle

open class Activity
open class Application : Context() {
    interface ActivityLifecycleCallbacks {
        fun onActivityStarted(activity: Activity)
        fun onActivityStopped(activity: Activity)
        fun onActivityCreated(activity: Activity, bundle: Bundle?)
        fun onActivityResumed(activity: Activity)
        fun onActivityPaused(activity: Activity)
        fun onActivitySaveInstanceState(activity: Activity, bundle: Bundle)
        fun onActivityDestroyed(activity: Activity)
    }
    fun registerActivityLifecycleCallbacks(callbacks: ActivityLifecycleCallbacks) { }
}
