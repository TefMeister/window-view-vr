"""echo_keys.py <out file> -- the throwaway target of keys_test.py: runs in its own Windows Terminal window (title WVKEYTEST)
and writes one line per key it reads: "<GetTickCount ms> <character code>". Stops at "~". Touches nothing else."""
import ctypes
import msvcrt
import sys

tick = ctypes.windll.kernel32.GetTickCount
out = sys.argv[1]
print("WVKEYTEST: waiting for keys (Window View keys_test.py); ~ ends")
with open(out, "w", encoding="utf-8") as f:
    while True:
        c = msvcrt.getwch()
        f.write("%d %d\n" % (tick(), ord(c)))
        f.flush()
        print(c, end="", flush=True)
        if c == "~":
            break
