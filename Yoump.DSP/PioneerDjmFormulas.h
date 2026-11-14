#pragma once
#include <cmath>

namespace yoump {

inline float DbToLinear(float db)
{
    return std::pow(10.0f, db / 20.0f);
}

// trimGain 0..2, 1.0 = 0 dB. Max boost +9 dB.
inline float CalculatePioneerTrimLinear(float trimGain0To2)
{
    const float knob = trimGain0To2 * 0.5f;
    if (knob <= 0.015f)
        return 0.0f;
    if (knob >= 0.492f && knob <= 0.508f)
        return 1.0f;
    if (knob > 0.5f)
    {
        const float clamped = knob < 1.0f ? knob : 1.0f;
        const float boostDb = ((clamped - 0.5f) / 0.5f) * 9.0f;
        return DbToLinear(boostDb);
    }
    const float norm = (knob - 0.015f) / 0.485f;
    return norm * norm;
}

// Isolator: kill at <= -11.7 dB, boost clamped to +6 dB, cubic cut.
inline float DbToIsolatorLinear(float db)
{
    if (db <= -11.7f)
        return 0.0f;
    if (std::fabs(db) < 0.05f)
        return 1.0f;
    if (db < 0.0f)
    {
        float norm = (db + 12.0f) / 12.0f;
        if (norm < 0.0f)
            norm = 0.0f;
        return norm * norm * norm;
    }
    const float boostDb = db < 6.0f ? db : 6.0f;
    return DbToLinear(boostDb);
}

constexpr float kLowMidCrossoverHz = 250.0f;
constexpr float kMidHighCrossoverHz = 3500.0f;

} // namespace yoump
