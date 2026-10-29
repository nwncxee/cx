// Self-check for the label walk and the report statistics. Oracle: hand-written timestamps and the durations
// they imply, worked out by hand below each case; nothing is computed by the code under test.
#define PASSTIME_TEST
#include "passtime.c"

#include <assert.h>
#include <math.h>
#include <unistd.h>

static Path *find(const char *path)
{
    for (int i = 0; i < g_npaths; i++)
        if (strcmp(g_paths[i].path, path) == 0) return &g_paths[i];
    return NULL;
}

static void reset(void)
{
    memset(g_paths, 0, MAX_PATHS * sizeof *g_paths);
    g_npaths = 0;
}

int main(void)
{
    g_paths = calloc(MAX_PATHS, sizeof *g_paths);

    /* frame{ a{} b{} } with ticks 100 a:110..140 b:150..190 end 200, period 2 ns
       -> frame 200 ns, a 60, b 80, frame/(gaps) 200-140 = 60 */
    int frame = intern(-1, "frame"), a = intern(frame, "a"), b = intern(frame, "b");
    Ev ev[] = { { 0, frame, 1 }, { 1, a, 1 }, { 2, a, 0 }, { 3, b, 1 }, { 4, b, 0 }, { 5, frame, 0 } };
    uint64_t ts[] = { 100, 110, 140, 150, 190, 200 };
    walk(ev, 6, ts, 2.0, 7);
    assert(find("frame")->n == 1 && find("frame")->ns[0] == 200.0);
    assert(find("frame/a")->ns[0] == 60.0 && find("frame/b")->ns[0] == 80.0);
    assert(find("frame/(gaps)") && find("frame/(gaps)")->ns[0] == 60.0);
    assert(find("frame")->frame[0] == 7);
    assert(!find("frame/a/(gaps)"));

    /* an end with nothing open is skipped; a begin never closed yields nothing */
    reset();
    int x = intern(-1, "x"), y = intern(-1, "y");
    Ev odd[] = { { 0, x, 0 }, { 1, y, 1 }, { 2, y, 0 }, { 3, x, 1 } };
    uint64_t t2[] = { 5, 10, 13, 20 };
    walk(odd, 4, t2, 1.0, 0);
    assert(find("y")->n == 1 && find("y")->ns[0] == 3.0);
    assert(find("x")->n == 0);

    /* nearest-rank quantiles of 1..5 */
    double s[] = { 1, 2, 3, 4, 5 };
    assert(quantile(s, 5, 0.5) == 3 && quantile(s, 5, 0.9) == 5 && quantile(s, 5, 0.0) == 1);

    /* the report keeps only the window and prints the median */
    reset();
    int p = intern(-1, "pass");
    for (int i = 0; i < 10; i++) add_sample(p, (i < 5 ? 9e6 : 1e6), (uint64_t)i);
    char out[] = "/tmp/passtime-test-XXXXXX";
    int fd = mkstemp(out);
    assert(fd >= 0);
    close(fd);
    write_report(out, 10, 5, 1.0);
    FILE *f = fopen(out, "r");
    char line[512], *row = NULL;
    while (fgets(line, sizeof line, f))
        if (strncmp(line, "pass ", 5) == 0) row = strdup(line);
    fclose(f);
    unlink(out);
    int n;
    double med;
    assert(row && sscanf(row, "pass %d %lf", &n, &med) == 2);
    assert(n == 5 && fabs(med - 1.0) < 1e-9);

    puts("passtime self-check: passed");
    return 0;
}
