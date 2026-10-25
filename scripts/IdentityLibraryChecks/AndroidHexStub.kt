package com.liuzhuan.app.core
fun ByteArray.hexString()=joinToString(""){"%02x".format(it)}
