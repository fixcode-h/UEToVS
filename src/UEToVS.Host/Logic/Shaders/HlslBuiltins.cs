using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Shaders
{
    public enum HlslBuiltinKind
    {
        Intrinsic,
        Method,
        Type,
        UeFunction,
        UeMacro,
        Keyword,
    }

    public sealed class HlslBuiltin
    {
        public HlslBuiltin(string name, HlslBuiltinKind kind, string signature, string zh, string en)
        {
            Name = name;
            Kind = kind;
            Signature = signature;
            DescriptionZh = zh;
            DescriptionEn = en;
        }

        public string Name { get; }
        public HlslBuiltinKind Kind { get; }
        public string Signature { get; }
        public string DescriptionZh { get; }
        public string DescriptionEn { get; }

        public string Description(bool english) => english ? DescriptionEn : DescriptionZh;
    }

    /// <summary>
    /// 补全和悬停用的 HLSL 内建函数、纹理方法、常用类型，以及 UE 着色器里最常用的函数和宏（Common.ush / Platform.ush 等，
    /// 加上编译时由 C++ 注入的 FEATURE_LEVEL、PLATFORM_* 这类宏）。只是一张静态表，不读引擎文件；引擎里真正的定义仍走转到定义。
    /// </summary>
    public static class HlslBuiltins
    {
        private static B I(string n, string sig, string zh, string en) => new B(n, HlslBuiltinKind.Intrinsic, sig, zh, en);
        private static B M(string n, string sig, string zh, string en) => new B(n, HlslBuiltinKind.Method, sig, zh, en);
        private static B T(string n, string zh, string en) => new B(n, HlslBuiltinKind.Type, n, zh, en);
        private static B U(string n, string sig, string zh, string en) => new B(n, HlslBuiltinKind.UeFunction, sig, zh, en);
        private static B D(string n, string zh, string en) => new B(n, HlslBuiltinKind.UeMacro, n, zh, en);

        private sealed class B
        {
            public readonly HlslBuiltin Value;
            public B(string n, HlslBuiltinKind k, string sig, string zh, string en) => Value = new HlslBuiltin(n, k, sig, zh, en);
        }

        private static readonly B[] Table =
        {
            // ---- 数学
            I("abs", "T abs(T x)", "绝对值（逐分量）。", "Absolute value (per component)."),
            I("acos", "T acos(T x)", "反余弦，x 在 [-1, 1]。", "Arccosine, x in [-1, 1]."),
            I("asin", "T asin(T x)", "反正弦。", "Arcsine."),
            I("atan", "T atan(T x)", "反正切，结果在 [-π/2, π/2]。", "Arctangent in [-π/2, π/2]."),
            I("atan2", "T atan2(T y, T x)", "y/x 的反正切，按象限给出 [-π, π]。", "Arctangent of y/x using the quadrant, in [-π, π]."),
            I("ceil", "T ceil(T x)", "向上取整。", "Round up."),
            I("floor", "T floor(T x)", "向下取整。", "Round down."),
            I("round", "T round(T x)", "四舍五入到最近的整数。", "Round to the nearest integer."),
            I("trunc", "T trunc(T x)", "去掉小数部分。", "Drop the fractional part."),
            I("frac", "T frac(T x)", "小数部分 x - floor(x)。", "Fractional part x - floor(x)."),
            I("fmod", "T fmod(T x, T y)", "x / y 的浮点余数（符号同 x）。", "Floating-point remainder of x / y (sign of x)."),
            I("clamp", "T clamp(T x, T min, T max)", "把 x 限制在 [min, max]。", "Clamp x to [min, max]."),
            I("saturate", "T saturate(T x)", "限制到 [0, 1]，通常免费（指令修饰符）。", "Clamp to [0, 1]; usually free (instruction modifier)."),
            I("lerp", "T lerp(T a, T b, T s)", "线性插值 a + s * (b - a)。", "Linear interpolation a + s * (b - a)."),
            I("smoothstep", "T smoothstep(T min, T max, T x)", "x 在 [min, max] 之间做 Hermite 平滑插值，结果在 [0, 1]。", "Hermite interpolation of x between min and max, result in [0, 1]."),
            I("step", "T step(T y, T x)", "x >= y 时为 1，否则为 0。", "1 when x >= y, else 0."),
            I("min", "T min(T a, T b)", "较小值（逐分量）。", "Minimum (per component)."),
            I("max", "T max(T a, T b)", "较大值（逐分量）。", "Maximum (per component)."),
            I("mad", "T mad(T a, T b, T c)", "a * b + c，可能编译成一条乘加指令。", "a * b + c, may compile to one multiply-add."),
            I("pow", "T pow(T x, T y)", "x 的 y 次方；x 为负时结果未定义（UE 里常用 PositiveClampedPow）。", "x to the power y; undefined for negative x (UE code often uses PositiveClampedPow)."),
            I("exp", "T exp(T x)", "e 的 x 次方。", "e to the power x."),
            I("exp2", "T exp2(T x)", "2 的 x 次方。", "2 to the power x."),
            I("log", "T log(T x)", "自然对数。", "Natural logarithm."),
            I("log2", "T log2(T x)", "以 2 为底的对数。", "Base-2 logarithm."),
            I("log10", "T log10(T x)", "以 10 为底的对数。", "Base-10 logarithm."),
            I("sqrt", "T sqrt(T x)", "平方根。", "Square root."),
            I("rsqrt", "T rsqrt(T x)", "平方根的倒数 1 / sqrt(x)。", "Reciprocal square root 1 / sqrt(x)."),
            I("rcp", "T rcp(T x)", "倒数（近似，精度低于 1 / x）。", "Reciprocal (approximate, less precise than 1 / x)."),
            I("sign", "int sign(T x)", "x 的符号：-1、0 或 1。", "Sign of x: -1, 0 or 1."),
            I("sin", "T sin(T x)", "正弦（弧度）。", "Sine (radians)."),
            I("cos", "T cos(T x)", "余弦（弧度）。", "Cosine (radians)."),
            I("tan", "T tan(T x)", "正切（弧度）。", "Tangent (radians)."),
            I("sincos", "void sincos(T x, out T s, out T c)", "同时求正弦和余弦。", "Sine and cosine at once."),
            I("sinh", "T sinh(T x)", "双曲正弦。", "Hyperbolic sine."),
            I("cosh", "T cosh(T x)", "双曲余弦。", "Hyperbolic cosine."),
            I("tanh", "T tanh(T x)", "双曲正切。", "Hyperbolic tangent."),
            I("degrees", "T degrees(T x)", "弧度转角度。", "Radians to degrees."),
            I("radians", "T radians(T x)", "角度转弧度。", "Degrees to radians."),
            I("isnan", "bool isnan(T x)", "是否为 NaN。", "Whether x is NaN."),
            I("isinf", "bool isinf(T x)", "是否为无穷大。", "Whether x is infinite."),
            I("isfinite", "bool isfinite(T x)", "是否为有限值。", "Whether x is finite."),
            I("ldexp", "T ldexp(T x, T exp)", "x * 2^exp。", "x * 2^exp."),
            I("frexp", "T frexp(T x, out T exp)", "拆成尾数和指数。", "Split into mantissa and exponent."),
            I("modf", "T modf(T x, out T ip)", "拆成小数部分（返回）和整数部分（ip）。", "Split into fractional part (returned) and integer part (ip)."),
            // ---- 向量 / 矩阵
            I("dot", "float dot(vector a, vector b)", "点积。", "Dot product."),
            I("cross", "float3 cross(float3 a, float3 b)", "叉积。", "Cross product."),
            I("length", "float length(vector v)", "向量长度。", "Vector length."),
            I("distance", "float distance(vector a, vector b)", "两点距离。", "Distance between two points."),
            I("normalize", "vector normalize(vector v)", "单位化；零向量结果为 NaN（UE 里可用 SafeNormalize）。", "Unit vector; NaN for a zero vector (UE has SafeNormalize)."),
            I("reflect", "vector reflect(vector i, vector n)", "按法线 n 求反射方向。", "Reflection of i about normal n."),
            I("refract", "vector refract(vector i, vector n, float eta)", "按折射率比 eta 求折射方向。", "Refraction of i through n with index ratio eta."),
            I("faceforward", "vector faceforward(vector n, vector i, vector ng)", "让 n 朝向与 i 相反的一侧。", "Flip n to face against i."),
            I("mul", "mul(a, b)", "矩阵 / 向量乘法；UE 约定行向量在左：mul(Position, Matrix)。", "Matrix / vector multiply; UE uses row vectors on the left: mul(Position, Matrix)."),
            I("transpose", "matrix transpose(matrix m)", "转置矩阵。", "Transpose."),
            I("determinant", "float determinant(matrix m)", "行列式。", "Determinant."),
            I("all", "bool all(T x)", "所有分量都不为 0。", "All components non-zero."),
            I("any", "bool any(T x)", "任一分量不为 0。", "Any component non-zero."),
            // ---- 位运算与类型转换
            I("asfloat", "float asfloat(T x)", "按位重解释为 float。", "Reinterpret the bits as float."),
            I("asint", "int asint(T x)", "按位重解释为 int。", "Reinterpret the bits as int."),
            I("asuint", "uint asuint(T x)", "按位重解释为 uint。", "Reinterpret the bits as uint."),
            I("f16tof32", "float f16tof32(uint x)", "低 16 位的半精度转 float。", "Half in the low 16 bits to float."),
            I("f32tof16", "uint f32tof16(float x)", "float 转半精度，放在低 16 位。", "Float to half, in the low 16 bits."),
            I("countbits", "uint countbits(uint x)", "为 1 的位数。", "Number of set bits."),
            I("firstbithigh", "int firstbithigh(int x)", "最高位 1 的位置。", "Position of the highest set bit."),
            I("firstbitlow", "int firstbitlow(uint x)", "最低位 1 的位置。", "Position of the lowest set bit."),
            I("reversebits", "uint reversebits(uint x)", "按位反转。", "Reverse the bits."),
            // ---- 导数（像素着色器）
            I("ddx", "T ddx(T x)", "屏幕 x 方向的偏导（像素着色器）。", "Screen-space x derivative (pixel shaders)."),
            I("ddy", "T ddy(T x)", "屏幕 y 方向的偏导（像素着色器）。", "Screen-space y derivative (pixel shaders)."),
            I("ddx_fine", "T ddx_fine(T x)", "逐像素精度的 x 偏导。", "Fine x derivative."),
            I("ddy_fine", "T ddy_fine(T x)", "逐像素精度的 y 偏导。", "Fine y derivative."),
            I("fwidth", "T fwidth(T x)", "abs(ddx(x)) + abs(ddy(x))。", "abs(ddx(x)) + abs(ddy(x))."),
            I("clip", "void clip(T x)", "任一分量小于 0 时丢弃当前像素。", "Discard the pixel when any component is below 0."),
            // ---- 同步与原子
            I("GroupMemoryBarrierWithGroupSync", "void GroupMemoryBarrierWithGroupSync()", "线程组内同步，并等 groupshared 写入完成。", "Sync the thread group and wait for groupshared writes."),
            I("GroupMemoryBarrier", "void GroupMemoryBarrier()", "等 groupshared 写入完成（不同步线程）。", "Wait for groupshared writes (no thread sync)."),
            I("AllMemoryBarrierWithGroupSync", "void AllMemoryBarrierWithGroupSync()", "线程组同步，并等所有内存写入完成。", "Sync the group and wait for all memory writes."),
            I("DeviceMemoryBarrier", "void DeviceMemoryBarrier()", "等显存（UAV）写入完成。", "Wait for device memory (UAV) writes."),
            I("InterlockedAdd", "void InterlockedAdd(inout uint dest, uint value, out uint original)", "原子加。", "Atomic add."),
            I("InterlockedMin", "void InterlockedMin(inout T dest, T value, out T original)", "原子取小。", "Atomic min."),
            I("InterlockedMax", "void InterlockedMax(inout T dest, T value, out T original)", "原子取大。", "Atomic max."),
            I("InterlockedOr", "void InterlockedOr(inout uint dest, uint value, out uint original)", "原子或。", "Atomic or."),
            I("InterlockedAnd", "void InterlockedAnd(inout uint dest, uint value, out uint original)", "原子与。", "Atomic and."),
            I("InterlockedExchange", "void InterlockedExchange(inout T dest, T value, out T original)", "原子交换。", "Atomic exchange."),
            I("InterlockedCompareExchange", "void InterlockedCompareExchange(inout T dest, T compare, T value, out T original)", "原子比较并交换。", "Atomic compare-exchange."),
            // ---- Wave（SM6）
            I("WaveGetLaneIndex", "uint WaveGetLaneIndex()", "当前线程在 wave 里的序号。", "Index of this lane in the wave."),
            I("WaveGetLaneCount", "uint WaveGetLaneCount()", "wave 宽度。", "Lanes per wave."),
            I("WaveIsFirstLane", "bool WaveIsFirstLane()", "是否为活动线程里序号最小的。", "Whether this is the lowest active lane."),
            I("WaveActiveSum", "T WaveActiveSum(T x)", "所有活动线程的和。", "Sum over the active lanes."),
            I("WaveActiveMin", "T WaveActiveMin(T x)", "所有活动线程的最小值。", "Minimum over the active lanes."),
            I("WaveActiveMax", "T WaveActiveMax(T x)", "所有活动线程的最大值。", "Maximum over the active lanes."),
            I("WaveActiveAnyTrue", "bool WaveActiveAnyTrue(bool x)", "任一活动线程为 true。", "Any active lane is true."),
            I("WaveActiveAllTrue", "bool WaveActiveAllTrue(bool x)", "所有活动线程都为 true。", "All active lanes are true."),
            I("WaveActiveCountBits", "uint WaveActiveCountBits(bool x)", "为 true 的活动线程数。", "Number of active lanes where x is true."),
            I("WaveReadLaneFirst", "T WaveReadLaneFirst(T x)", "读第一个活动线程的值。", "Value of the first active lane."),
            I("WaveReadLaneAt", "T WaveReadLaneAt(T x, uint lane)", "读指定线程的值。", "Value of the given lane."),
            I("WavePrefixSum", "T WavePrefixSum(T x)", "序号更小的活动线程之和。", "Sum over the active lanes with a lower index."),
            // ---- 纹理 / 缓冲区方法
            M("Sample", "T Tex.Sample(SamplerState S, float2 UV [, int2 Offset])", "按 mip 自动选择采样（只在像素着色器里有导数）。", "Sample with automatic mip selection (derivatives exist in pixel shaders only)."),
            M("SampleLevel", "T Tex.SampleLevel(SamplerState S, float2 UV, float Mip)", "按指定 mip 级别采样，任何阶段都能用。", "Sample a given mip level; works in every stage."),
            M("SampleGrad", "T Tex.SampleGrad(SamplerState S, float2 UV, float2 DDX, float2 DDY)", "按给定导数采样。", "Sample with explicit derivatives."),
            M("SampleBias", "T Tex.SampleBias(SamplerState S, float2 UV, float Bias)", "带 mip 偏移采样。", "Sample with a mip bias."),
            M("SampleCmp", "float Tex.SampleCmp(SamplerComparisonState S, float2 UV, float Ref)", "比较采样（阴影贴图）。", "Comparison sample (shadow maps)."),
            M("SampleCmpLevelZero", "float Tex.SampleCmpLevelZero(SamplerComparisonState S, float2 UV, float Ref)", "mip 0 上的比较采样。", "Comparison sample on mip 0."),
            M("Load", "T Tex.Load(int3 Location)", "按整数坐标读取，不过滤；Location.z 为 mip。", "Read by integer coordinates, no filtering; Location.z is the mip."),
            M("Gather", "float4 Tex.Gather(SamplerState S, float2 UV)", "取 2x2 邻域四个像素的一个通道。", "One channel of the 2x2 footprint."),
            M("GetDimensions", "void Tex.GetDimensions(out uint Width, out uint Height …)", "查询纹理 / 缓冲区尺寸。", "Query the texture / buffer size."),
            M("Append", "void Buffer.Append(T value)", "向 AppendStructuredBuffer 追加。", "Append to an AppendStructuredBuffer."),
            M("Consume", "T Buffer.Consume()", "从 ConsumeStructuredBuffer 取出。", "Take from a ConsumeStructuredBuffer."),
            M("IncrementCounter", "uint Buffer.IncrementCounter()", "UAV 计数器加一，返回旧值。", "Increment the UAV counter, returns the old value."),
            // ---- 类型
            T("Texture2D", "2D 纹理，Texture2D<float4>。", "2D texture, Texture2D<float4>."),
            T("Texture2DArray", "2D 纹理数组。", "2D texture array."),
            T("Texture3D", "体积纹理。", "Volume texture."),
            T("TextureCube", "立方体贴图。", "Cube map."),
            T("RWTexture2D", "可读写的 2D 纹理（UAV）。", "Read-write 2D texture (UAV)."),
            T("RWTexture3D", "可读写的体积纹理（UAV）。", "Read-write volume texture (UAV)."),
            T("Buffer", "类型化缓冲区。", "Typed buffer."),
            T("RWBuffer", "可读写的类型化缓冲区（UAV）。", "Read-write typed buffer (UAV)."),
            T("StructuredBuffer", "结构化缓冲区，StructuredBuffer<FMyStruct>。", "Structured buffer, StructuredBuffer<FMyStruct>."),
            T("RWStructuredBuffer", "可读写的结构化缓冲区（UAV）。", "Read-write structured buffer (UAV)."),
            T("ByteAddressBuffer", "按字节寻址的原始缓冲区，Load / Load4。", "Raw buffer addressed in bytes, Load / Load4."),
            T("RWByteAddressBuffer", "可读写的原始缓冲区（UAV）。", "Read-write raw buffer (UAV)."),
            T("SamplerState", "采样器。", "Sampler."),
            T("SamplerComparisonState", "比较采样器（阴影）。", "Comparison sampler (shadows)."),
            T("groupshared", "线程组共享内存。", "Thread-group shared memory."),
            // ---- UE 常用函数（Common.ush 等）
            U("Square", "T Square(T x)", "UE：x * x。", "UE: x * x."),
            U("Pow2", "T Pow2(T x)", "UE：x²。", "UE: x squared."),
            U("Pow4", "T Pow4(T x)", "UE：x⁴。", "UE: x to the 4th."),
            U("Pow5", "T Pow5(T x)", "UE：x⁵（Schlick 菲涅耳常用）。", "UE: x to the 5th (Schlick Fresnel)."),
            U("PositiveClampedPow", "T PositiveClampedPow(T Base, T Exponent)", "UE：底数先限制为非负再 pow，避免负数 NaN。", "UE: pow with the base clamped to ≥ 0, no NaN for negatives."),
            U("Luminance", "float Luminance(float3 LinearColor)", "UE：线性颜色的亮度。", "UE: luminance of a linear colour."),
            U("SafeNormalize", "float3 SafeNormalize(float3 V)", "UE：零向量时不出 NaN 的 normalize。", "UE: normalize without NaN for a zero vector."),
            U("Texture2DSample", "float4 Texture2DSample(Texture2D Tex, SamplerState S, float2 UV)", "UE：跨平台的 2D 纹理采样封装。", "UE: cross-platform 2D texture sample wrapper."),
            U("Texture2DSampleLevel", "float4 Texture2DSampleLevel(Texture2D Tex, SamplerState S, float2 UV, float Mip)", "UE：指定 mip 的 2D 纹理采样。", "UE: 2D texture sample at a mip."),
            U("TextureCubeSample", "float4 TextureCubeSample(TextureCube Tex, SamplerState S, float3 UV)", "UE：立方体贴图采样封装。", "UE: cube map sample wrapper."),
            U("TextureCubeSampleLevel", "float4 TextureCubeSampleLevel(TextureCube Tex, SamplerState S, float3 UV, float Mip)", "UE：指定 mip 的立方体贴图采样。", "UE: cube map sample at a mip."),
            U("ConvertFromDeviceZ", "float ConvertFromDeviceZ(float DeviceZ)", "UE：深度缓冲里的设备 Z 转成线性场景深度。", "UE: device Z from the depth buffer to linear scene depth."),
            U("ConvertToDeviceZ", "float ConvertToDeviceZ(float SceneDepth)", "UE：线性场景深度转设备 Z。", "UE: linear scene depth to device Z."),
            U("SvPositionToBufferUV", "float2 SvPositionToBufferUV(float4 SvPosition)", "UE：SV_Position 转缓冲区 UV。", "UE: SV_Position to buffer UV."),
            U("SvPositionToTranslatedWorld", "float3 SvPositionToTranslatedWorld(float4 SvPosition)", "UE：SV_Position 转平移后的世界坐标。", "UE: SV_Position to translated world position."),
            U("GetScreenPositionForProjectionType", "float2 GetScreenPositionForProjectionType(float2 ScreenPosition, float SceneDepth)", "UE：按透视 / 正交投影换算屏幕坐标。", "UE: screen position for the projection type."),
            U("CalcSceneDepth", "float CalcSceneDepth(float2 ScreenUV)", "UE：从场景深度纹理读线性深度。", "UE: linear depth from the scene depth texture."),
            U("DecodeSceneColorForMaterialNode", "float4 DecodeSceneColorForMaterialNode(float2 ScreenUV)", "UE：材质里读场景颜色。", "UE: scene colour for material nodes."),
            U("MulMatrix", "MulMatrix(Matrix, Vector)", "UE：在 LWC / DF 矩阵上做乘法。", "UE: multiply with LWC / DF matrices."),
            U("DFPromote", "FDFVector3 DFPromote(float3 V)", "UE5：float 提升为双精度（Double Float）类型。", "UE5: promote a float to a double-float type."),
            U("DFDemote", "float3 DFDemote(FDFVector3 V)", "UE5：双精度类型降为 float。", "UE5: demote a double-float to float."),
            U("LWCToFloat", "float3 LWCToFloat(FLWCVector3 V)", "UE5：大世界坐标转 float。", "UE5: large-world coordinate to float."),
            U("ResolvedView", "ResolvedView", "UE：当前视图的参数（ResolveView() 的结果，含 Stereo 处理）。", "UE: the current view's parameters (result of ResolveView(), stereo aware)."),
            U("View", "View", "UE：视图 uniform buffer（矩阵、屏幕尺寸、时间等）。", "UE: the view uniform buffer (matrices, screen size, time…)."),
            U("Primitive", "Primitive", "UE：当前图元的 uniform 数据（GetPrimitiveData 取）。", "UE: the current primitive's data (via GetPrimitiveData)."),
            U("GetPrimitiveData", "FPrimitiveSceneData GetPrimitiveData(FMaterialVertexParameters Parameters)", "UE：取当前图元的场景数据。", "UE: scene data of the current primitive."),
            // ---- UE 宏（编译环境 / Platform.ush）
            D("FEATURE_LEVEL", "UE：当前编译的特性级别，和 FEATURE_LEVEL_SM5 / FEATURE_LEVEL_SM6 / FEATURE_LEVEL_ES3_1 比较。", "UE: the feature level being compiled; compare with FEATURE_LEVEL_SM5 / SM6 / ES3_1."),
            D("FEATURE_LEVEL_SM5", "UE：特性级别 SM5。", "UE: feature level SM5."),
            D("FEATURE_LEVEL_SM6", "UE：特性级别 SM6。", "UE: feature level SM6."),
            D("FEATURE_LEVEL_ES3_1", "UE：移动端特性级别 ES3.1。", "UE: mobile feature level ES3.1."),
            D("SHADING_PATH_DEFERRED", "UE：延迟渲染路径时为 1。", "UE: 1 on the deferred shading path."),
            D("SHADING_PATH_MOBILE", "UE：移动渲染路径时为 1。", "UE: 1 on the mobile shading path."),
            D("COMPILER_HLSL", "UE：用 HLSL 编译器（DXC / FXC）时为 1。", "UE: 1 when compiled with an HLSL compiler (DXC / FXC)."),
            D("COMPILER_DXC", "UE：用 DXC 编译时为 1。", "UE: 1 when compiled with DXC."),
            D("COMPILER_SUPPORTS_WAVE_ONCE", "UE：平台支持 wave 内建函数时为 1。", "UE: 1 when wave intrinsics are supported."),
            D("PLATFORM_SUPPORTS_SRV_UB", "UE：平台支持 uniform buffer 里放 SRV。", "UE: the platform supports SRVs in uniform buffers."),
            D("SM6_PROFILE", "UE：按 SM6 编译时为 1。", "UE: 1 when compiling for SM6."),
            D("MATERIALBLENDING_MASKED", "UE：材质混合模式为 Masked。", "UE: the material blend mode is Masked."),
            D("MATERIALBLENDING_TRANSLUCENT", "UE：材质混合模式为 Translucent。", "UE: the material blend mode is Translucent."),
            D("NUM_MATERIAL_TEXCOORDS", "UE：材质用到的 UV 数。", "UE: number of texture coordinates the material uses."),
            D("THREADGROUP_SIZEX", "UE：常由 C++ SetDefine 注入的线程组 X 尺寸。", "UE: thread group X size, usually set from C++ with SetDefine."),
            D("THREADGROUP_SIZEY", "UE：常由 C++ SetDefine 注入的线程组 Y 尺寸。", "UE: thread group Y size, usually set from C++ with SetDefine."),
            D("PI", "UE：圆周率 3.1415926535897932。", "UE: pi, 3.1415926535897932."),
            D("INVARIANT", "UE：标记输出为 precise / invariant，保证多遍渲染位置一致。", "UE: marks an output invariant so positions match across passes."),
            D("UNROLL", "UE：[unroll] 循环展开提示。", "UE: [unroll] loop hint."),
            D("LOOP", "UE：[loop] 不展开循环提示。", "UE: [loop] do-not-unroll hint."),
            D("BRANCH", "UE：[branch] 真分支提示。", "UE: [branch] real-branch hint."),
            D("FLATTEN", "UE：[flatten] 展平分支提示。", "UE: [flatten] flatten-branch hint."),
            D("ISOLATE", "UE：阻止编译器跨这段代码重排（平台支持时）。", "UE: keeps the compiler from reordering across this block (where supported)."),
            D("SHADER_PARAMETER", "UE（C++ 侧）：BEGIN_SHADER_PARAMETER_STRUCT 里声明一个参数，shader 里同名全局变量接收。", "UE (C++ side): declares a parameter in BEGIN_SHADER_PARAMETER_STRUCT; a same-named global receives it."),
            D("DECLARE_SCALAR_ARRAY", "UE：声明按 float4 打包的标量数组参数。", "UE: declares a scalar array packed in float4s."),
            D("GET_SCALAR_ARRAY_ELEMENT", "UE：读 DECLARE_SCALAR_ARRAY 声明的数组元素。", "UE: reads an element of a DECLARE_SCALAR_ARRAY array."),
        };

        private static readonly Dictionary<string, HlslBuiltin> ByName = Build();

        private static Dictionary<string, HlslBuiltin> Build()
        {
            var d = new Dictionary<string, HlslBuiltin>(StringComparer.Ordinal);
            foreach (var b in Table) d[b.Value.Name] = b.Value;
            return d;
        }

        public static IEnumerable<HlslBuiltin> All
        {
            get { foreach (var b in Table) yield return b.Value; }
        }

        public static int Count => Table.Length;

        public static HlslBuiltin Get(string name) => name != null && ByName.TryGetValue(name, out var b) ? b : null;

        /// <summary>名字后面跟 '.' 时（纹理 / 缓冲区方法）只给方法，否则给方法以外的项。</summary>
        public static IEnumerable<HlslBuiltin> For(bool afterDot)
        {
            foreach (var b in Table)
                if ((b.Value.Kind == HlslBuiltinKind.Method) == afterDot) yield return b.Value;
        }

        public static string KindText(HlslBuiltinKind kind, bool english)
        {
            switch (kind)
            {
                case HlslBuiltinKind.Intrinsic: return english ? "HLSL intrinsic" : "HLSL 内建函数";
                case HlslBuiltinKind.Method: return english ? "texture / buffer method" : "纹理 / 缓冲区方法";
                case HlslBuiltinKind.Type: return english ? "HLSL type" : "HLSL 类型";
                case HlslBuiltinKind.UeFunction: return english ? "UE shader function" : "UE 着色器函数";
                case HlslBuiltinKind.UeMacro: return english ? "UE shader macro" : "UE 着色器宏";
                default: return english ? "keyword" : "关键字";
            }
        }
    }
}
