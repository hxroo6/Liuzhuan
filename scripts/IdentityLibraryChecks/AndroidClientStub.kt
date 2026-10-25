package com.liuzhuan.app.net
import org.json.JSONObject
/** Only transport is substituted; the production page coordinator/repository/codec run unchanged. */
class LanClient(val libraryId:String,val handle:suspend (JSONObject)->JSONObject) {
    val librarySupported=true
    val libraryEditSupported=true
    suspend fun library(data:JSONObject):JSONObject {check(data.optString("libraryId",libraryId)==libraryId){"wrong library"};return handle(data.put("libraryId",libraryId))}
}
