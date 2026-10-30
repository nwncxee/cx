# ADR-0027: The audio runtime ships as separate, unmodified libraries beside the extender, which links neither.

Category: Dependencies.

Decided 2026-09-27.

Because OpenAL Soft is LGPL and the system copy varies, so a replaceable file avoids relinking obligations and pins the version.
