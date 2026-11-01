# ADR-0054: First-use pipelines link from libraries on the frame; one worker builds the optimised link.

Category: Architecture.

Decided 2026-11-01.

Because a full compile on the frame stalls it and a fast link costs a millisecond. The worker writes only the pipeline cache, which Vulkan synchronises, so ADR-0022 still holds for Alloy's own data.
