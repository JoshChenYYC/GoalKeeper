# Camera binding

GoalKeeper's native camera wrapper uses
[`OpenCvSharp4`](https://www.nuget.org/packages/OpenCvSharp4/) and its matching
Windows runtime package, both pinned to `4.13.0.20260627`. OpenCvSharp is the
.NET binding for the same OpenCV capture and JPEG primitives used by the Python
reference.

Only `GoalKeeper.Infrastructure` references OpenCvSharp. Application contracts
expose lifecycle operations, health, raw-frame handles, immutable JPEG results,
and technical camera events without exposing `VideoCapture`, `Mat`, or any
other binding-specific type. Automated contract tests replace the native
wrapper and never open a webcam. A real webcam check remains an explicitly
consented acceptance activity under GK-002/GK-016.
