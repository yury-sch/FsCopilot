#pragma once

#include "protocol.h"
#include "interpolators.h"
#include "stream_buffer.h"
#include "time_shift.h"

constexpr double k_delay_sec        = 0.75;
constexpr double k_max_age_sec      = 3.0;
constexpr double k_offset_smoothing = 0.01;