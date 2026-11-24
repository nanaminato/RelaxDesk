#pragma once
#include <dxgiformat.h>

// DuplicateOutput1 may return an application's original scan-out buffer.
// RGB10 has no transfer-function metadata: output HDR state does not prove
// that those pixels are PQ. Request scRGB or the DXGI compatibility formats.
inline constexpr DXGI_FORMAT desktop_formats[]={
    DXGI_FORMAT_R16G16B16A16_FLOAT,
    DXGI_FORMAT_B8G8R8A8_UNORM,
    DXGI_FORMAT_R8G8B8A8_UNORM
};
