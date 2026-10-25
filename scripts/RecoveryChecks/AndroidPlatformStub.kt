package android.net
data class Uri(val value:String) {override fun toString()=value;companion object {fun parse(value:String)=Uri(value);fun fromFile(file:java.io.File)=Uri(file.toURI().toString())}}
