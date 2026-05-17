Import("env")

# Match build.bat's link line: -flto -Wl,--gc-sections -Wl,--print-memory-usage
# plus the vendored libgcc.a from ch32v003fun/.
env.Append(LINKFLAGS=[
    "-flto",
    "-Wl,--print-memory-usage",
])

# Use the vendored libgcc.a (build.bat: -L"ch32v003fun" -lgcc).
# This pins the helper-runtime to the exact bytes the existing
# dali_bootloader.bin was linked against — avoiding accidental size
# variance vs. the working reference output.
env.Append(LIBPATH=["$PROJECT_DIR/ch32v003fun"])
env.Append(LIBS=["gcc"])

# _bare.py adds -lm (math). The BL does not use any libm symbol, but
# --gc-sections strips it anyway. Leaving -lm in place is harmless.
