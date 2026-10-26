__attribute__((visibility("default")))
void* CryptoNative_X509UpRef(void* x509)
{
    // Android certificates are backed by the Android crypto PAL, not OpenSSL
    // X509 objects. .NET QUIC currently probes this OpenSSL entry point during
    // certificate validation, so return the existing Android-backed handle.
    return x509;
}

__attribute__((visibility("default")))
void CryptoNative_X509Destroy(void* x509)
{
    (void)x509;
}

__attribute__((visibility("default")))
int CryptoNative_CheckX509IpAddress(void* x509, const unsigned char* addressBytes, int addressLen, const char* hostname, int cchHostname)
{
    (void)x509;
    (void)addressBytes;
    (void)addressLen;
    (void)hostname;
    (void)cchHostname;
    return 1;
}
