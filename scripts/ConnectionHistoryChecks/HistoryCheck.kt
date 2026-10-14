package com.liuzhuan.checks

import com.liuzhuan.app.core.*
import javax.crypto.KeyGenerator
import java.util.Base64

private var count=0
private fun verify(value:Boolean,message:String) {check(value){message};count++}
fun main() {
    val key=KeyGenerator.getInstance("AES").apply {init(256)}.generateKey()
    val cipher=HistoryPasswordCipher {key}
    val password="history-test-only-口令"
    val first=cipher.seal(password)
    verify(cipher.open(first)==password,"production AES-GCM Unicode round trip")
    verify(!first.contains(password),"no plaintext in saved credential")
    verify(cipher.seal(password)!=first,"each encryption uses a fresh IV")
    val damaged=Base64.getDecoder().decode(first.substring(3)).apply {this[lastIndex]=(this[lastIndex].toInt() xor 1).toByte()}
    verify(runCatching {cipher.open("v1:"+Base64.getEncoder().encodeToString(damaged))}.isFailure,"modified ciphertext rejected")
    val other=HistoryPasswordCipher {KeyGenerator.getInstance("AES").apply {init(256)}.generateKey()}
    verify(runCatching {other.open(first)}.isFailure,"wrong device key rejected")
    verify(runCatching {cipher.open("v2:unknown")}.isFailure,"unknown credential format rejected")
    verify(runCatching {cipher.open("v1:AA==")}.isFailure,"truncated credential rejected")

    val one=ConnectionProfile("PC.example.test",8899,100,first)
    var list=ConnectionProfiles.remember(emptyList(),one)
    verify(list==listOf(one),"first successful target added")
    val refreshed=one.copy(host="pc.example.test",lastConnected=200,sealedPassword=cipher.seal("updated-test-only"))
    list=ConnectionProfiles.remember(list,refreshed)
    verify(list.size==1 && list.single()==refreshed,"same endpoint updates credential and time instead of duplicating")
    list=ConnectionProfiles.remember(list,one.copy(port=9000,lastConnected=300))
    verify(list.size==2 && list.first().port==9000,"different port remains a distinct target")
    repeat(25) {list=ConnectionProfiles.remember(list,one.copy(host="pc-$it.example.test",lastConnected=1000L+it))}
    verify(list.size==20 && list.first().host=="pc-24.example.test","bounded newest-first history")
    verify(list.last().lastConnected==1005L,"oldest entries evicted")
    val json=ConnectionProfiles.encode(list)
    verify(ConnectionProfiles.decode(json)==list,"production JSON round trip")
    verify(!json.contains(password) && !json.contains("\"password\":"),"serialized profiles contain ciphertext only")
    verify(ConnectionProfiles.decode("").isEmpty(),"new installs start empty")
    verify(runCatching {ConnectionProfiles.decode("not-json")}.isFailure,"corrupt storage is rejected for caller recovery")
    verify(runCatching {ConnectionProfiles.decode(json.replace("\"version\":1","\"version\":2"))}.isFailure,"unknown schema cannot silently erase history")
    verify(runCatching {ConnectionProfiles.encode(listOf(one,one.copy(host="pc.example.test")))}.isSuccess &&
        runCatching {ConnectionProfiles.decode(ConnectionProfiles.encode(listOf(one,one.copy(host="pc.example.test"))))}.isFailure,"duplicate stored identities rejected")
    verify(runCatching {ConnectionProfiles.remember(emptyList(),one.copy(port=0))}.isFailure,"invalid saved port rejected")
    verify(runCatching {ConnectionProfiles.remember(emptyList(),one.copy(host=" "))}.isFailure,"blank target rejected")
    verify(runCatching {ConnectionProfiles.remember(emptyList(),one.copy(sealedPassword=password))}.isFailure,"plaintext credential cannot enter history")
    verify(list.filter {it.id!=list.first().id}.size==19,"deleting one identity leaves other targets intact")
    println("PASS: $count connection-history checks; production profile codec and AES-GCM with JVM test key")
}
