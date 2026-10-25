package com.liuzhuan.checks

import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import java.io.File
import java.security.MessageDigest

fun main(args:Array<String>) {
    val records=FileRecords(File(args[0],"original-records"));val folder=File(args[0],"original-payloads")
    val target="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:11111111-1111-1111-1111-111111111111"
    val foreign="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb:11111111-1111-1111-1111-111111111111"
    val c=OfflineOriginalCatalog(records,folder);c.setLimit(128L*1024*1024)
    var count=0;fun verify(ok:Boolean,text:String){check(ok){text};count++;println("PASS $text")}
    val bytes=ByteArray(4096){(it%251).toByte()};val source=File(args[0],"verified-transfer-payload").apply{writeBytes(bytes)}
    val hash=MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(""){"%02x".format(it)}
    val item=MaterialItem("image","Image","image.png",bytes.size.toLong(),0,sourceRevision=3)
    val p=c.pin(target,item,"session-one")
    verify(c.pin(target,item).key==p.key&&c.snapshot().pins.size==1,"same source version pin reuses its exact reservation")
    verify(OfflineOriginalCatalog.key(foreign,item.id,3)!=p.key&&OfflineOriginalCatalog.key(target,item.id,4)!=p.key,"computer identity, library identity and source revision isolate original keys")
    c.pin(target,item.copy(id="large-one",size=60L*1024*1024));c.pin(target,item.copy(id="large-two",size=60L*1024*1024))
    verify(runCatching{c.pin(target,item.copy(id="overflow",size=10L*1024*1024))}.isFailure&&c.snapshot().pins.size==3,"pending downloads consume quota before IO and over-budget pin is rejected atomically")
    verify(runCatching{c.prepareCopy(p,source,bytes.size.toLong(),"0".repeat(64)){true}}.isFailure&&c.readyFile(p.key)==null,"bad content hash never becomes offline ready")
    val prepared=c.prepareCopy(p,source,bytes.size.toLong(),hash){true};c.commitCopy(p.key,prepared,hash,p.token)
    verify(c.verify(p.key)&&source.exists(),"verified private copy survives independently of original reliable-transfer payload")
    val fresh=OfflineOriginalCatalog(records,folder)
    verify(fresh.snapshot().pins.first().token==p.token&&fresh.snapshot().pins.first().sourceRun=="session-one"&&fresh.verify(p.key),"pin token, historical session and SHA survive a new catalog instance")
    val existing=fresh.readyFile(p.key)!!;val altered=bytes.clone();altered[0]=(altered[0]+1).toByte();existing.writeBytes(altered)
    verify(!fresh.verify(p.key),"same-size private-file corruption is detected by full verification")
    val staged=c.prepareCopy(p,source,bytes.size.toLong(),hash){true};c.unpin(p.key);val newer=c.pin(target,item,"session-two")
    verify(newer.token!=p.token&&runCatching{c.commitCopy(p.key,staged,hash,p.token)}.isFailure,"unpin and re-pin token rejects an old completion for the same item and revision")
    c.discardCopy(staged);c.unpin(p.key)
    verify(source.exists()&&c.readyFile(p.key)==null,"unpin deletes only module-owned private bytes and never the received source file")
    val interrupted=File(folder,"${p.key}.partial-11111111-1111-1111-1111-111111111111").apply {writeBytes(bytes)}
    verify(c.snapshot().orphanBytes==bytes.size.toLong()&&c.cleanOrphans()==1&&!interrupted.exists(),"interrupted private-copy bytes count against quota and explicit orphan cleanup removes only module temporary files")
    records.fail="library-r05-originals";val before=c.snapshot().pins
    verify(runCatching{c.pin(target,item.copy(id="io-fail"))}.isFailure&&c.snapshot().pins==before,"catalog IO failure never reports a successful pin or mutates memory")
    verify(runCatching{OfflineOriginalCatalog.key("../wrong",item.id,3)}.isFailure,"invalid target identifiers are rejected before creating payload paths")
    println("PASS $count offline original catalog checks; encrypted record adapter and Android provider tested separately")
}
