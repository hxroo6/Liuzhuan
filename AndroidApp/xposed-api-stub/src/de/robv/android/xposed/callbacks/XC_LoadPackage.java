package de.robv.android.xposed.callbacks;
public abstract class XC_LoadPackage {
    protected XC_LoadPackage() {}
    public static class LoadPackageParam {
        public String packageName;
        public ClassLoader classLoader;
        public LoadPackageParam() {}
    }
}
