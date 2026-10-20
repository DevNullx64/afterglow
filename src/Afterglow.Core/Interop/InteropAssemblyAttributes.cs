using System.Runtime.CompilerServices;

// Afterglow.Core never relies on the runtime's built-in marshaller: every
// P/Invoke is [LibraryImport] (source-generated, pointer-only inner stubs), and
// every NVAPI call goes through an unmanaged function pointer whose parameters
// are primitives or pointers to blittable structs. Disabling runtime
// marshalling makes that a rule rather than a convention — a struct or
// signature that would need hidden conversion cannot slip in — and pins the
// semantics the driver structs depend on: `char` is UTF-16 (NvU16), and an
// unmanaged struct crosses as its exact managed bytes (the layouts pinned by
// InteropLayoutTests). Scope: this assembly only (Vortice and other references
// keep their own settings).
[assembly: DisableRuntimeMarshalling]
