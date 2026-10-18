namespace AtmosBridge;

/// <summary>
/// Streaming Catmull-Rom resampler for tiny ratio changes (clock drift between mpv's video clock and the DAC).
/// At ratio 1 it is an exact passthrough (two samples of delay).
/// </summary>
sealed class DriftResampler {
    const int history = 3;
    float[][] buffer; // [0..2] tail of the previous block, then the current block
    double position = 1; // read position in buffer, in input samples

    public Chunk Process(Chunk input, double ratio) {
        int channels = input.Samples.Length, n = input.Length;
        if (buffer == null || buffer.Length != channels) {
            buffer = new float[channels][];
            position = 1;
        }
        for (int ch = 0; ch < channels; ch++) {
            if (buffer[ch] == null || buffer[ch].Length != n + history) {
                var resized = new float[n + history];
                if (buffer[ch] != null) {
                    Array.Copy(buffer[ch], resized, history);
                }
                buffer[ch] = resized;
            }
            input.Samples[ch].CopyTo(buffer[ch], history);
        }

        double step = 1 / ratio;
        int count = 0;
        for (double p = position; p < n + 1; p += step) {
            count++;
        }
        var output = new float[channels][];
        for (int ch = 0; ch < channels; ch++) {
            float[] b = buffer[ch], o = output[ch] = new float[count];
            double p = position;
            for (int k = 0; k < count; k++, p += step) {
                int i = (int)p;
                float t = (float)(p - i), x0 = b[i - 1], x1 = b[i], x2 = b[i + 1], x3 = b[i + 2];
                o[k] = x1 + t * (.5f * (x2 - x0) + t * (x0 - 2.5f * x1 + 2 * x2 - .5f * x3 + t * (.5f * (x3 - x0) + 1.5f * (x1 - x2))));
            }
            Array.Copy(b, n, b, 0, history);
        }
        position += count * step - n;
        return input with { Samples = output };
    }
}

/// <summary>
/// Keeps the bridge buffer at its target level by nudging the resampling ratio (PI control, ±0.2 %).
/// </summary>
sealed class DriftController(double targetSamples, double sampleRate) {
    double average = -1, integral;

    public double Ratio { get; private set; } = 1;

    /// <param name="fill">Samples buffered between decoder and output</param>
    /// <param name="active">Output is consuming (not paused, not prebuffering)</param>
    /// <param name="dt">Seconds of input since the last update</param>
    public void Update(long fill, bool active, double dt) {
        if (!active) {
            average = -1; // keep the learned integral, restart the level average
            return;
        }
        average = average < 0 ? fill : average + (fill - average) * Math.Min(1, dt); // ~1 s smoothing
        if (average > 2 * targetSamples) {
            Ratio = 1; // back-pressured (no video, mpv untimed throttle off): mpv follows our clock already
            return;
        }
        double error = (average - targetSamples) / sampleRate; // seconds above target
        integral = Math.Clamp(integral + error * dt, -4, 4);
        Ratio = Math.Clamp(1 - (.005 * error + .0005 * integral), .998, 1.002);
    }
}
