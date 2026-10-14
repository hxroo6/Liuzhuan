package com.liuzhuan.app.core

/** Wi-Fi 客户端网络与手机自身热点是独立链路；串行发布避免回调交错误断开。 */
class LanRouteState<K>(private val onChanged: (Boolean) -> Unit) {
    private val networks = mutableMapOf<K, Boolean>()
    private var wifiHotspot = false
    private var published: Boolean? = null

    @Synchronized fun updateNetwork(network: K, isLan: Boolean) {
        networks[network] = isLan
        publish()
    }

    @Synchronized fun removeNetwork(network: K) {
        networks.remove(network)
        publish()
    }

    @Synchronized fun updateWifiHotspot(active: Boolean) {
        wifiHotspot = active
        publish()
    }

    @Synchronized fun publish() {
        val available = wifiHotspot || networks.values.any { it }
        if (published != available) {
            published = available
            onChanged(available)
        }
    }
}
