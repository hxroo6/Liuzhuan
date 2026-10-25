package com.liuzhuan.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.google.zxing.BarcodeFormat
import com.journeyapps.barcodescanner.*

/** A dedicated portrait camera: orientation is independent of ZXing's landscape CaptureActivity. */
class PairingScanActivity : ComponentActivity() {
    private var camera: DecoratedBarcodeView? = null
    private var allowed by mutableStateOf(false)
    private var error by mutableStateOf("")
    private var torch by mutableStateOf(false)
    private var frame by mutableStateOf<android.graphics.Rect?>(null)
    private var completed = false
    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        allowed = it
        if (!it) error = "需要相机权限才能扫描电脑上的配对码"
        else camera?.resume()
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        allowed = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (!allowed) permission.launch(Manifest.permission.CAMERA)
        setContent {
            FlowTheme {
                val ink=MaterialTheme.colorScheme.background;val mint=MaterialTheme.colorScheme.primary
                Column(Modifier.fillMaxSize().background(ink).safeDrawingPadding().padding(20.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        TextButton(onClick = { finish() }) { Text("返回") }
                        Spacer(Modifier.weight(1f)); Text("流转  /  LIUZHUAN", color = mint, style = MaterialTheme.typography.labelLarge)
                    }
                    Text("连接，从这里开始", color = MaterialTheme.colorScheme.onBackground, style = MaterialTheme.typography.headlineMedium)
                    Text("对准电脑流转的配对二维码", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyLarge)
                    Surface(Modifier.fillMaxWidth().weight(1f), shape = RoundedCornerShape(28.dp), color = MaterialTheme.colorScheme.surface) {
                        Box(contentAlignment = Alignment.Center) {
                            if (allowed) AndroidView(modifier = Modifier.fillMaxSize(), factory = { context ->
                                DecoratedBarcodeView(context).also { view ->
                                    camera = view
                                    view.setStatusText("")
                                    view.viewFinder.setLaserVisibility(false)
                                    view.viewFinder.setMaskColor(android.graphics.Color.TRANSPARENT)
                                    view.barcodeView.addOnLayoutChangeListener { _,_,_,_,_,_,_,_,_ -> frame=view.barcodeView.framingRect?.let { android.graphics.Rect(it) } }
                                    view.decoderFactory = DefaultDecoderFactory(listOf(BarcodeFormat.QR_CODE))
                                    view.barcodeView.addStateListener(object : CameraPreview.StateListener {
                                        override fun previewSized() { view.post { frame=view.barcodeView.framingRect?.let { android.graphics.Rect(it) } } }
                                        override fun previewStarted() { error = ""; view.post { frame=view.barcodeView.framingRect?.let { android.graphics.Rect(it) } } }
                                        override fun previewStopped() = Unit
                                        override fun cameraClosed() = Unit
                                        override fun cameraError(exception: Exception) { error = "相机暂不可用，请关闭其他相机应用后重试" }
                                    })
                                    view.decodeSingle { result ->
                                        if (!completed && !result.text.isNullOrBlank()) {
                                            completed = true; view.pause()
                                            setResult(RESULT_OK, Intent().putExtra("SCAN_RESULT", result.text).putExtra("SCAN_RESULT_FORMAT", "QR_CODE"))
                                            finish()
                                        }
                                    }
                                    view.resume()
                                }
                            })
                            Canvas(Modifier.matchParentSize()) {
                                val rect=frame ?: return@Canvas
                                val x=rect.left.toFloat();val y=rect.top.toFloat();val right=rect.right.toFloat();val bottom=rect.bottom.toFloat();val arm=28.dp.toPx()
                                for ((px,py,sx,sy) in listOf(listOf(x,y,1f,1f),listOf(right,y,-1f,1f),listOf(x,bottom,1f,-1f),listOf(right,bottom,-1f,-1f))) {
                                    drawLine(mint, Offset(px,py), Offset(px+sx*arm,py), 4.dp.toPx(), StrokeCap.Round)
                                    drawLine(mint, Offset(px,py), Offset(px,py+sy*arm), 4.dp.toPx(), StrokeCap.Round)
                                }
                            }
                            if (error.isNotEmpty()) Surface(color = ink.copy(alpha=.95f), shape = RoundedCornerShape(16.dp), modifier = Modifier.padding(24.dp)) {
                                Column(Modifier.padding(20.dp)) {
                                    Text(error, color = Color.White)
                                    TextButton(onClick = { if (!allowed) permission.launch(Manifest.permission.CAMERA) else { error=""; camera?.resume() } }) { Text("重试") }
                                }
                            }
                        }
                    }
                    Text("一次扫码 · 素材同步与手机麦克风\n收音遵循已保存偏好；节能连接不开麦，首次开启需允许录音。", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
                    OutlinedButton(onClick = { torch = !torch; if (torch) camera?.setTorchOn() else camera?.setTorchOff() }, enabled = allowed && packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_FLASH), modifier = Modifier.fillMaxWidth().height(52.dp)) {
                        Text(if (torch) "关闭手电筒" else "打开手电筒")
                    }
                }
            }
        }
    }
    override fun onResume() { super.onResume(); if (allowed && !completed) camera?.resume() }
    override fun onPause() { camera?.setTorchOff(); torch = false; camera?.pause(); super.onPause() }
    override fun onDestroy() { camera?.pause(); camera = null; super.onDestroy() }
}
