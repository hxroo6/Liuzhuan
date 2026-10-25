import android.content.Context
import android.net.Uri
import com.liuzhuan.app.*
import com.liuzhuan.app.net.Proto
import com.liuzhuan.app.core.hexString
import kotlinx.coroutines.*
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

private suspend fun awaitTask(label:String,condition:()->Boolean) {
    try {withTimeout(40_000){while(!condition())delay(5)}}
    catch(ex:Exception){println(TransferTasks.tasks.value.map {"${it.id}: ${it.state}, ${it.done}/${it.total} ${it.error}"});error(label)}
}
private fun verify(ok:Boolean,label:String){check(ok){label};println("PASS $label")}
fun runChecks(args:Array<String>):Unit=runBlocking {
    val phase=args[0];val root=File(args[1]).apply {mkdirs()};val host=JSONObject(File(args[2]).readText())
    val auth=MessageDigest.getInstance("SHA-256").digest("isolated-recovery-test".toByteArray()).hexString()
    val link=DurableTransfers.Link("127.0.0.1",host.getInt("port"),auth,host.getString("serverId"),if(phase=="upload-pause")"fixture-original-computer" else "fixture-stable-computer",if(phase=="upload-pause")"" else "fixture-original-computer")
    val client=FixtureClient(link);LanHub.client=client;val context=Context(root);DurableTransfers.init(context)
    fun row(id:String)=TransferTasks.tasks.value.firstOrNull {it.id==id}
    if(phase=="upload-pause") {
        val source=File(root,"phone-source.bin")
        source.outputStream().use {output->val block=ByteArray(65536){(it%251).toByte()};repeat(512){output.write(block)};output.write(byteArrayOf(1,2,3))}
        verify(DurableTransfers.upload(context,Uri.fromFile(source),"fixture-upload","android",null,{true},{_,_->}),"production upload negotiates the resumable route")
        awaitTask("upload did not reach first committed block"){row("fixture-upload")?.let {it.running && it.done>=1048576 && it.done<it.total && TransferTasks.canPause(it.id)}==true}
        TransferTasks.pause("fixture-upload")
        awaitTask("pause did not stop IO"){row("fixture-upload")?.let {it.paused && !it.running}==true && TransferWork.active.value==0}
        verify(row("fixture-upload")!!.done in 1048576 until source.length(),"pause retains a nonzero partial byte offset")
        delay(150);println("CLIENT_PHASE_DONE")
    }else if(phase=="upload-resume-download-pause") {
        awaitTask("paused upload not restored"){row("fixture-upload")?.paused==true}
        delay(100);verify(TransferWork.active.value==0,"fresh client process restores manual pause without resuming IO")
        client.link=link.copy(serverId="different-server");TransferTasks.resume("fixture-upload")
        awaitTask("target mismatch not visible"){row("fixture-upload")?.failed==true}
        verify(row("fixture-upload")!!.error.contains("原电脑"),"explicit retry refuses a different authenticated computer identity")
        client.link=link
        val descriptor=File(root,"records").listFiles()!!.filter {it.name.startsWith("file-") && it.name!="file-index.json"}.map {JSONObject(it.readText())}.single {it.optString("localId")=="fixture-upload"}
        verify(descriptor.optString("target")=="fixture-stable-computer","authenticated same-library upgrade rebinds a paused legacy-address task")
        val taskId=descriptor.getString("id");val revision=descriptor.optLong("controlRevision")+1
        DurableTransfers.remoteState("wrong-server",taskId,"active",revision);delay(50)
        verify(!TransferTasks.running("fixture-upload"),"foreign server cannot issue a remote continue command")
        DurableTransfers.remoteState(link.serverId,taskId,"active",revision)
        DurableTransfers.remoteState(link.serverId,taskId,"paused",revision-1)
        awaitTask("upload did not finish"){row("fixture-upload")?.let {!it.running && !it.failed && !it.paused && it.state=="电脑已持久登记"}==true}
        verify(row("fixture-upload")!!.done==File(root,"phone-source.bin").length(),"new process resumes remaining upload and receives the durable material receipt")
        verify(DurableTransfers.download(Proto.ItemData("fixture-source","source.bin")),"production download negotiates the resumable route")
        awaitTask("download did not reach first saved block"){TransferTasks.tasks.value.any {it.direction=="电脑 → 手机" && it.running && it.done>=1048576 && it.done<it.total}}
        val download=TransferTasks.tasks.value.first {it.direction=="电脑 → 手机"};TransferTasks.pause(download.id)
        awaitTask("download pause did not stop"){row(download.id)?.let {it.paused && !it.running}==true && TransferWork.active.value==0}
        verify(File(root,"media").listFiles().orEmpty().none {it.extension=="json"},"partial download is not published as a received file")
        delay(150);println("CLIENT_PHASE_DONE")
    }else {
        awaitTask("paused download not restored"){TransferTasks.tasks.value.any {it.direction=="电脑 → 手机" && it.paused}}
        val download=TransferTasks.tasks.value.first {it.direction=="电脑 → 手机"};TransferTasks.resume(download.id)
        awaitTask("publication fault not observed"){row(download.id)?.let {it.failed && it.error.contains("injected failure")}==true}
        val media=File(root,"media")
        verify(media.listFiles()!!.count {it.extension=="json"}==1,"publication fault leaves one recoverable MediaStore row")
        TransferTasks.retry(download.id)
        awaitTask("publication replay failed"){row(download.id)?.let {!it.running && !it.failed && it.state=="已校验并保存到手机"}==true}
        verify(media.listFiles()!!.count {it.extension=="json"}==1,"retry after publication reuses the original saved URI without a duplicate file")
        val saved=media.listFiles()!!.single {it.extension=="bin"};val expected=File(args[3])
        fun hash(file:File):String {val digest=MessageDigest.getInstance("SHA-256");file.inputStream().use {input->val b=ByteArray(65536);while(true){val n=input.read(b);if(n<0)break;digest.update(b,0,n)}};return digest.digest().hexString()}
        verify(saved.length()==expected.length() && hash(saved)==hash(expected),"resumed and published download matches PC source SHA-256 exactly")
        verify(row(download.id)!!.done==expected.length() && row(download.id)!!.total==expected.length(),"resume of a fully staged download preserves completed byte counts in the task UI")
        verify(row(download.id)!!.savedFolder==ReceivedFiles.FOLDER,"resumed receive still uses the fixed Download/Liuzhuan destination")
        val original=File(root,"phone-source.bin")
        DurableTransfers.upload(context,Uri.fromFile(original),"expired-upload","android",null,{true},{_,_->})
        awaitTask("expiry fixture did not begin"){row("expired-upload")?.let {it.running && it.done>=1048576 && it.done<it.total && TransferTasks.canPause(it.id)}==true}
        TransferTasks.pause("expired-upload")
        awaitTask("expiry fixture did not pause"){row("expired-upload")?.let {it.paused && !it.running}==true && TransferWork.active.value==0}
        delay(150)
        val expiredDescriptor=File(root,"records").listFiles()!!.filter {it.name.startsWith("file-") && it.name!="file-index.json"}.map {JSONObject(it.readText())}.single {it.optString("localId")=="expired-upload"}
        val expiredKey=MessageDigest.getInstance("SHA-256").digest((Proto.clientId+":"+expiredDescriptor.getString("id")).toByteArray()).hexString()
        val serverRecord=File(expected.parentFile,"transfers-v1/$expiredKey.json")
        check(serverRecord.delete()){"fixture server record not found"}
        TransferTasks.resume("expired-upload")
        awaitTask("missing remote task was not surfaced"){row("expired-upload")?.state=="续传已过期"}
        verify(!serverRecord.exists() && !TransferTasks.canRetry("expired-upload"),"missing remote task is not blindly recreated after its receipt retention window")
        TransferTasks.cancel("expired-upload")
        awaitTask("expired payload did not clean up"){!File(root,"transfer-payloads/${expiredDescriptor.getString("id")}.payload").exists()}
        verify(File(root,"media").listFiles()!!.count {it.extension=="bin"}==1,"canceling expired recovery never deletes an already published receive file")
        System.setProperty("fixture.cancelOnPersist","prepare-cancel")
        DurableTransfers.upload(context,Uri.fromFile(original),"prepare-cancel","android",null,{true},{_,_->})
        awaitTask("prepare cancellation not durable"){row("prepare-cancel")?.let {it.cancelled && !it.running}==true && TransferWork.active.value==0}
        val cancelledDescriptor=File(root,"records").listFiles()!!.filter {it.name.startsWith("file-") && it.name!="file-index.json"}.map {JSONObject(it.readText())}.single {it.optString("localId")=="prepare-cancel"}
        verify(cancelledDescriptor.optString("state")=="cancelled" && !File(root,"transfer-payloads/${cancelledDescriptor.getString("id")}.payload").exists(),"cancel during descriptor commit stays cancelled after preparation and releases its private snapshot")
        println("CLIENT_PHASE_DONE")
    }
}

fun main(args:Array<String>){var code=0;try{runChecks(args)}catch(ex:Throwable){ex.printStackTrace();code=1}finally{kotlin.system.exitProcess(code)}}
