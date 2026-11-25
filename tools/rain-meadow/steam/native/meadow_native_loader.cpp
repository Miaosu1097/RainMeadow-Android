#include <cstdint>
#include <dlfcn.h>
#include <jni.h>

namespace {
void ThrowLinkError(JNIEnv* env, const char* message) {
    jclass error = env->FindClass("java/lang/UnsatisfiedLinkError");
    if (error != nullptr)
        env->ThrowNew(error, message != nullptr ? message : "Unknown dynamic linker error");
}
}

extern "C" JNIEXPORT jlong JNICALL
Java_rwmeadow_steam_NativeSteamLoader_openLoaded(JNIEnv* env, jobject, jstring path) {
    const char* name = env->GetStringUTFChars(path, nullptr);
    if (name == nullptr) return 0;

    // dlopen sees this JNI library as the caller, so it uses the same namespace as System.load.
    void* library = dlopen(name, RTLD_NOW | RTLD_NOLOAD);
    if (library == nullptr) ThrowLinkError(env, dlerror());
    env->ReleaseStringUTFChars(path, name);
    return static_cast<jlong>(reinterpret_cast<uintptr_t>(library));
}

extern "C" JNIEXPORT jlong JNICALL
Java_rwmeadow_steam_NativeSteamLoader_resolve(JNIEnv* env, jobject, jlong handle, jstring symbol) {
    const char* name = env->GetStringUTFChars(symbol, nullptr);
    if (name == nullptr) return 0;

    void* address = dlsym(reinterpret_cast<void*>(static_cast<uintptr_t>(handle)), name);
    if (address == nullptr) ThrowLinkError(env, dlerror());
    env->ReleaseStringUTFChars(symbol, name);
    return static_cast<jlong>(reinterpret_cast<uintptr_t>(address));
}
