#pragma once

struct time_shift
{
    bool   init      = false;
    double offset_ms = 0.0; // localMs - clientMs

    double to_local_ms(const double local_now_sec, const uint32_t client_ms, const double smoothing = 0.01)
    {
        const double local_ms = local_now_sec * 1000.0;

        if (!init)
        {
            offset_ms = local_ms - static_cast<double>(client_ms);
            init      = true;
        }
        else
        {
            const double desired = local_ms - static_cast<double>(client_ms);
            offset_ms += (desired - offset_ms) * smoothing;
        }

        return static_cast<double>(client_ms) + offset_ms;
    }

    double to_local_sec(const double local_now_sec, const uint32_t client_ms, const double smoothing = 0.01)
    {
        return to_local_ms(local_now_sec, client_ms, smoothing) / 1000.0;
    }
};