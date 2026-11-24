// turn_test.cpp -- 1.15: prints where the layer hangs each window and which way it faces, for the same grid of cases as
// `WindowView.exe --dump-frames`, by compiling the layer's very own sums (layer/face.inl). test/turn_test.py runs both and
// checks the app's hit test (Pointer.cs WindowFrame.Of) aims at exactly the window the layer draws. Built by build.bat.
//
//   turn_test.exe   -> one line per case: room distance x y depth tilt turn | centre | right | up | normal
#include <openxr/openxr.h>
#include <cmath>
#include <cstdio>

namespace {
#include "../layer/face.inl"
}

int main() {
    const float rooms[] = {0, 1}, dists[] = {1.0f, 1.3f}, xs[] = {-0.4f, 0.0f, 0.5f}, ys[] = {-0.2f, 0.15f}, depths[] = {1.0f, 1.7f};
    const float tilts[] = {-40, 0, 25, 70}, turns[] = {-60, 0, 35};
    const float rad = 3.14159265358979f / 180.0f;
    for (float room : rooms) for (float dist : dists) for (float x : xs) for (float y : ys) for (float depth : depths)
        for (float tilt : tilts) for (float turn : turns) {
            XrVector3f p{};
            XrQuaternionf face{};
            window_pose(x, y, depth, dist, room != 0, tilt * rad, turn * rad, p, face);
            const XrVector3f r = q_rot(face, {1, 0, 0}), u = q_rot(face, {0, 1, 0}), n = q_rot(face, {0, 0, 1});
            std::printf("%g %g %g %g %g %g %g | %.6f %.6f %.6f | %.6f %.6f %.6f | %.6f %.6f %.6f | %.6f %.6f %.6f\n", room, dist, x, y, depth,
                        tilt, turn, p.x, p.y, p.z, r.x, r.y, r.z, u.x, u.y, u.z, n.x, n.y, n.z);
        }
    return 0;
}
