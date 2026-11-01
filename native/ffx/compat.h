// Force-included (-include) into AMD's FidelityFX sources on Linux: the MSVC CRT names they call.
#pragma once
#ifdef __cplusplus
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>

#define _countof(a) (sizeof(a) / sizeof((a)[0]))
#define FFX_UNUSED(x) ((void)(x))

inline int wcscpy_s(wchar_t* d, size_t n, const wchar_t* s) { if (!d || !n) return 1; wcsncpy(d, s ? s : L"", n - 1); d[n - 1] = 0; return 0; }
template <size_t N> inline int wcscpy_s(wchar_t (&d)[N], const wchar_t* s) { return wcscpy_s(d, N, s); }
template <typename... A> inline int sprintf_s(char* d, size_t n, const char* f, A... a) { return snprintf(d, n, f, a...); }
template <size_t N, typename... A> inline int sprintf_s(char (&d)[N], const char* f, A... a) { return snprintf(d, N, f, a...); }
template <typename... A> inline int swprintf_s(wchar_t* d, size_t n, const wchar_t* f, A... a) { return swprintf(d, n, f, a...); }
template <size_t N, typename... A> inline int swprintf_s(wchar_t (&d)[N], const wchar_t* f, A... a) { return swprintf(d, N, f, a...); }
#endif
