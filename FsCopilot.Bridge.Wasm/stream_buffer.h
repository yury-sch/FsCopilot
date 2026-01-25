#pragma once
#include <deque>

template <typename T> struct timed_sample
{
    double t_local; // local seconds
    T      value;
};

template <typename T> class stream_buffer
{
  public:
    void push(double t_local, const T& v)
    {
        prune(t_local);
        buf_.push_back({t_local, v});
    }

    template <typename LerpFn> bool interpolate(double t_render, T& out, LerpFn lerp_fn) const
    {
        const auto n = buf_.size();
        // if (n < 2)
        //     return false;

        if (n == 0)
            return false;

        // if only one sample, we can only hold it.
        if (n == 1)
        {
            out = buf_[0].value;
            return true;
        }

        // if asked time is after the last sample, hold last.
        if (t_render >= buf_[n - 1].t_local)
        {
            out = buf_[n - 1].value;
            return true;
        }

        // if asked time is before the first sample, hold first.
        if (t_render <= buf_[0].t_local)
        {
            out = buf_[0].value;
            return true;
        }

        // find segment [i, i+1] containing t_render
        uint32_t i = 0;
        while (i + 1 < n && buf_[i + 1].t_local < t_render)
            ++i;

        // i+1 must exist because we handled t_render < last above.
        // if (i + 1 >= n)
        //     return false;

        const auto& a = buf_[i];
        const auto& b = buf_[i + 1];

        // if (t_render < a.t_local || t_render > b.t_local)
        //     return false;

        const double dt    = b.t_local - a.t_local;
        const double alpha = dt <= 1e-9 ? 0.0 : (t_render - a.t_local) / dt;

        lerp_fn(a.value, b.value, alpha, out);
        return true;
    }

  private:
    std::deque<timed_sample<T>> buf_;

    static constexpr double k_max_age_sec = 3.0;

    void prune(const double now)
    {
        const double min_t = now - k_max_age_sec;
        while (!buf_.empty() && buf_.front().t_local < min_t)
            buf_.pop_front();
    }
};
