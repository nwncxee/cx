// Wrappers for engine functions that throw C++ exceptions (DirectPInvoke "cxthunk", linked into cx_managed on both
// OSes). A managed hook may never call such a function itself: the unwind would cross a NativeAOT frame. So funchook
// points the engine here instead, the managed callbacks run and return on either side, and the original is called
// from this C frame, which the unwinder passes through (it has unwind tables and no handler).
// Also the crash report's stamp, which runs where managed code cannot: inside the engine's crash handler.

#define _CRT_SECURE_NO_WARNINGS
#include <stdio.h>
#include <string.h>

/// kiwi::impl::SolverImpl::optimize(Row const&) throws InternalSolverError("The objective is unbounded.").
/// before: NuiSolver on Windows, where optimize inlines substitute. after: NuiLayoutErrors, which reads a failed
/// artificial objective. Either may be null.
typedef void (*cx_optimize_fn)(void *solver, const void *objective);
static cx_optimize_fn cx_optimize_before, cx_optimize_after, cx_optimize_orig;

static void cx_optimize(void *solver, const void *objective)
{
    if (cx_optimize_before) cx_optimize_before(solver, objective);
    cx_optimize_orig(solver, objective);
    if (cx_optimize_after) cx_optimize_after(solver, objective);
}

/// Stores the callbacks and returns the function to hook optimize with.
void *cx_optimize_thunk(cx_optimize_fn before, cx_optimize_fn after)
{
    cx_optimize_before = before;
    cx_optimize_after = after;
    return (void *)cx_optimize;
}

/// Stores the trampoline funchook returned; set before the hooks commit.
void cx_optimize_set_orig(cx_optimize_fn orig)
{
    cx_optimize_orig = orig;
}

/// CrashReporter_{Linux,Windows}::ReportCrashToUser(title, text): the engine's crash handler calls it last, after the
/// report file is closed, with text that ends "- The error report file at <path>.nwcrash.txt.". The crashed thread may
/// be a managed one, which cannot re-enter managed code, so the extender's notice is stamped here in C: on top of the
/// report file, and in place of the dialog's Beamdog paragraph.
typedef void (*cx_report_fn)(void *self, const char *title, const char *text);
static cx_report_fn cx_report_orig;
static char cx_report_banner[1024];
static char cx_report_text[0x2000];
static char cx_report_buf[0x10000];

/// Rewrites the report file as the banner followed by the engine's report. Gives up, leaving the file as it was,
/// on anything unexpected: the engine's report matters more than the stamp.
static void cx_report_stamp(const char *text)
{
    static const char at[] = "- The error report file at ", ext[] = ".nwcrash.txt";
    const char *p = strstr(text, at);
    if (!p) return;
    p += sizeof at - 1;
    const char *end = strstr(p, ext);
    if (!end) return;
    size_t n = (size_t)(end - p) + sizeof ext - 1;
    char path[4096], old[4096 + 8];
    if (n >= sizeof path) return;
    memcpy(path, p, n);
    path[n] = 0;
    snprintf(old, sizeof old, "%s.orig", path);
    if (rename(path, old) != 0) return;
    FILE *in = fopen(old, "rb");
    FILE *out = in ? fopen(path, "wb") : NULL;
    if (!out)
    {
        if (in) fclose(in);
        rename(old, path);
        return;
    }
    fprintf(out, "=== NWNCX:EE\n%s\n\n", cx_report_banner);
    size_t k;
    while ((k = fread(cx_report_buf, 1, sizeof cx_report_buf, in)) > 0) fwrite(cx_report_buf, 1, k, out);
    fclose(in);
    fclose(out);
    remove(old);
}

static void cx_report(void *self, const char *title, const char *text)
{
    if (!text) { cx_report_orig(self, title, text); return; }
    cx_report_stamp(text);
    const char *files = strstr(text, "Attach the following files:");
    snprintf(cx_report_text, sizeof cx_report_text, "%s\n\n%s", cx_report_banner, files ? files : text);
    cx_report_orig(self, "NWN:EE crashed with NWNCX:EE loaded", cx_report_text);
}

/// Stores the notice (copied) and returns the function to hook ReportCrashToUser with.
void *cx_report_thunk(const char *banner)
{
    snprintf(cx_report_banner, sizeof cx_report_banner, "%s", banner);
    return (void *)cx_report;
}

/// Stores the trampoline funchook returned; set before the hooks commit.
void cx_report_set_orig(cx_report_fn orig)
{
    cx_report_orig = orig;
}

/// /crashplease: a real access violation in a C frame, so the engine's own handler (SIGSEGV on Linux, its unhandled
/// exception filter on Windows) takes it exactly as it would a genuine crash. Volatile, so the store is not folded
/// into a trap instruction the handler does not catch.
void cx_crash(void)
{
    volatile int *volatile p = 0;
    *p = 0;
}

#ifndef _WIN32
#include <signal.h>
#include <unistd.h>

/// CrashReporter_Linux::InternalCrashHandler(int): the engine's signal handler. It writes the report with stdio and
/// shows a dialog, so a crash during the client's exit (display closed, stdio half torn down) crashes inside it again,
/// and the process then spins or wedges instead of ending. Here a second entry, or any entry once the client is
/// quitting, says so on stderr and ends the process; the first crash of a running client still gets its report.
typedef void (*cx_crash_handler_fn)(int sig);
static cx_crash_handler_fn cx_crash_handler_orig;
static volatile sig_atomic_t cx_crash_depth, cx_quitting;

static void cx_crash_say(const char *what, int sig)
{
    static const char head[] = "NWNCX:EE: signal ", tail[] = " -- the process exits here instead of re-entering the "
                                                            "engine's crash handler\n";
    char num[4] = { (char)('0' + sig / 10 % 10), (char)('0' + sig % 10), ' ', 0 };
    (void)!write(2, head, sizeof head - 1);
    (void)!write(2, sig >= 10 ? num : num + 1, sig >= 10 ? 3 : 2);
    (void)!write(2, what, strlen(what));
    (void)!write(2, tail, sizeof tail - 1);
}

static void cx_crash_handler(int sig)
{
    if (cx_crash_depth++ > 0) { cx_crash_say("inside the crash handler", sig); _exit(128 + sig); }
    if (cx_quitting) { cx_crash_say("while the client was quitting", sig); _exit(128 + sig); }
    cx_crash_handler_orig(sig);
}

void *cx_crash_handler_thunk(void) { return (void *)cx_crash_handler; }

void cx_crash_handler_set_orig(cx_crash_handler_fn orig) { cx_crash_handler_orig = orig; }

/// The client has begun quitting (its window is going): from here a crash ends the process at once.
void cx_crash_quitting(void) { cx_quitting = 1; }
#else
// Windows: the engine crashes through its unhandled-exception filter, not this handler; the managed side never calls these.
void *cx_crash_handler_thunk(void) { return 0; }
void cx_crash_handler_set_orig(void (*orig)(int)) { (void)orig; }
void cx_crash_quitting(void) { }
#endif
