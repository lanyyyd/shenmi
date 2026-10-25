# kotlinx.serialization 的 @Serializable 通过生成代码工作，不需要额外 keep 规则；
# 这里只保留 OkHttp 平台相关（避免 TLS 探测被裁剪）。
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
