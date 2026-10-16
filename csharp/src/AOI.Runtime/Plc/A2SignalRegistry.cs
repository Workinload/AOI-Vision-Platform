using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace AOI.Runtime.Plc;

/// <summary>
/// Immutable, evidence-labelled engineering registry. No field-verified PLC mapping is implied.
/// The embedded CSV is an exact textual snapshot of reference/spec_v2_machine_readable,
/// not a mutable runtime configuration. Each signal exposes all 21 source fields plus
/// parsed start/end and overlap evidence (24 fields in total).
/// </summary>
public sealed record A2PlcSignal(IReadOnlyDictionary<string, string> Fields, ushort StartWord, ushort EndWord)
{
    public string Id => Fields["SignalId"];
    public string Address => Fields["PLCAddress"];
    public string ValidationState => Fields["ValidationState"];
    public string ConflictGroup => Fields["ConflictGroup"];
    public bool ProductionWriteEnabled =>
        string.Equals(Fields["ProductionWriteEnabled"], "TRUE", StringComparison.OrdinalIgnoreCase);
}

public sealed record A2FieldGate(IReadOnlyDictionary<string, string> Fields)
{
    public string Id => Fields["GateId"];
    public string Status => Fields["Status"];
    public bool ProductionWriteAllowed =>
        string.Equals(Fields["ProductionWriteAllowed"], "TRUE", StringComparison.OrdinalIgnoreCase);
}

public static class A2SignalRegistry
{
    public const string SourceSignals = "reference/spec_v2_machine_readable/plc_signal_registry.csv";
    public const string SourceGates = "reference/spec_v2_machine_readable/field_verification_gates.csv";

    public static IReadOnlyList<A2PlcSignal> Signals { get; } = LoadSignals();
    public static IReadOnlyList<A2FieldGate> FieldGates { get; } = LoadGates();

    static A2SignalRegistry()
    {
        if (Signals.Count != 45 || FieldGates.Count != 14 ||
            FieldGates.Count(x => x.Status == "BLOCKED") != 7 ||
            FieldGates.Count(x => x.Status == "OPEN") != 7 ||
            Signals.Any(x => x.ProductionWriteEnabled) ||
            FieldGates.Any(x => x.ProductionWriteAllowed))
            throw new InvalidDataException("A2 static PLC evidence has changed: fail closed until A0 reviews.");
    }

    public static IReadOnlyList<A2PlcSignal> AtWord(ushort dmWord) =>
        Signals.Where(x => x.StartWord <= dmWord && dmWord <= x.EndWord).ToArray();

    public static A2PlcSignal Require(string id) =>
        Signals.Single(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    private static IReadOnlyList<A2PlcSignal> LoadSignals()
    {
        var records = ReadCsv(RawSignals);
        var spans = records.Select(fields =>
        {
            string address = fields["PLCAddress"];
            var segments = address.Split("..", StringSplitOptions.None);
            if (segments.Length is < 1 or > 2 ||
                !TryParseDm(segments[0], out var first) ||
                !TryParseDm(segments[^1], out var last) || first > last)
                throw new InvalidDataException("Invalid DM range in static signal registry: " + address);
            return (fields, first, last);
        }).ToArray();

        return new ReadOnlyCollection<A2PlcSignal>(spans.Select((entry, i) =>
        {
            bool overlaps = spans.Where((_, j) => j != i)
                .Any(other => entry.first <= other.last && other.first <= entry.last);
            var metadata = new Dictionary<string, string>(entry.fields, StringComparer.Ordinal)
            {
                ["ParsedStartWord"] = entry.first.ToString(CultureInfo.InvariantCulture),
                ["ParsedEndWord"] = entry.last.ToString(CultureInfo.InvariantCulture),
                ["OverlapDetected"] = overlaps ? "TRUE" : "FALSE"
            };
            if (metadata.Count != 24)
                throw new InvalidDataException("Static PLC signal has fewer than 24 fields.");
            return new A2PlcSignal(new ReadOnlyDictionary<string, string>(metadata), entry.first, entry.last);
        }).ToArray());
    }

    private static IReadOnlyList<A2FieldGate> LoadGates() =>
        new ReadOnlyCollection<A2FieldGate>(ReadCsv(RawGates).Select(row =>
            new A2FieldGate(new ReadOnlyDictionary<string, string>(row))).ToArray());

    private static bool TryParseDm(string text, out ushort address) =>
        text.StartsWith('D') &&
        ushort.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out address);

    /// <summary>CSV parser retaining quoted comma/escaped quote and the complete original columns.</summary>
    private static List<Dictionary<string, string>> ReadCsv(string source)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        void CompleteCell() { cells.Add(field.ToString()); field.Clear(); }
        void CompleteRow()
        {
            CompleteCell();
            if (cells.Count != 1 || cells[0].Length != 0) rows.Add(cells.ToArray());
            cells.Clear();
        }

        source = source.TrimStart('\uFEFF');
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"')
            {
                if (quoted && i + 1 < source.Length && source[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) CompleteCell();
            else if ((c == '\r' || c == '\n') && !quoted)
            {
                if (c == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                CompleteRow();
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unterminated quoted CSV value.");
        if (field.Length > 0 || cells.Count > 0) CompleteRow();
        if (rows.Count < 2) throw new InvalidDataException("Empty field-evidence CSV.");
        var header = rows[0];
        if (header.Length != header.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("Duplicate evidence CSV field.");
        return rows.Skip(1).Select(row =>
        {
            if (row.Length != header.Length)
                throw new InvalidDataException("PLC evidence CSV field count mismatch.");
            return header.Zip(row, (key, value) => (key, value))
                .ToDictionary(x => x.key, x => x.value, StringComparer.Ordinal);
        }).ToList();
    }

    private const string RawSignals = """
SignalId,PLCAddress,StationId,HostRole,Direction,DataType,EffectiveWords,PurposeCN,ValueLegend,ValidationState,RiskNote,EvidenceSource,EditClass,PhysicalDeviceId,EvidenceLevel,LastFieldVerifiedUtc,ActivationAllowed,ValueOrigin,MonitorReadOnlyDefault,ProductionWriteEnabled,ConflictGroup
SIG_HEARTBEAT_M,D99,MACHINE,MASTER,BIDIR,WORD,1,主机PLC心跳,1=PLC请求;2=上位机回应,LEGACY_STATIC_NEEDS_FIELD,旧上位机实现入口未核实,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_HEARTBEAT_S,D100,MACHINE,SLAVE,UNVERIFIED,WORD,1,从机心跳声明,"PLC声明,活跃时序未找到",DECLARATION_ONLY,不得假定等同D99,FINS完整契约说明.md,PLC_MAPPING_EDIT,,DECLARATION_ONLY,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_LEGACY_RESET_M,D101,MACHINE,MASTER,UNVERIFIED,WORD,1,PLC旧软件复位声明,旧源码未见活动引用,DECLARATION_ONLY,不能当受控复位入口,FINS完整契约说明.md,PLC_MAPPING_EDIT,,DECLARATION_ONLY,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_LEGACY_RESET_S,D102,MACHINE,SLAVE,UNVERIFIED,WORD,1,PLC旧软件复位声明,旧源码未见活动引用,DECLARATION_ONLY,不能当受控复位入口,FINS完整契约说明.md,PLC_MAPPING_EDIT,,DECLARATION_ONLY,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_POS,D1006,LOADING,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,上料定位采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,定位完成前必须确认XYU写入组,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-M-1,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_FRONT_SHORT,D1007,ST03,SLAVE,PLC_REQUEST/PC_REPLY,WORD,1,前短边采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,按真实PLC硬触发帧校验,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-S-1,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_FRONT_TAB,D1008,ST03,SLAVE,PLC_REQUEST/PC_REPLY,WORD,1,正面极耳采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,按真实PLC硬触发帧校验,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-S-2,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_LEFT_LONG,D1009,ST04,SLAVE,PLC_REQUEST/PC_REPLY,WORD,1,左长边采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,按真实PLC硬触发帧校验,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-S-3,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_REAR_TAB,D1010,ST04,SLAVE,PLC_REQUEST/PC_REPLY,WORD,1,反面极耳采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,按真实PLC硬触发帧校验,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-S-4,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_RIGHT_LONG,D1011,ST05,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,右长边采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,按真实PLC硬触发帧校验,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-M-2,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_REAR_SHORT,D1012,ST06,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,后短边采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,按真实PLC硬触发帧校验,FINS完整契约说明.md,PLC_MAPPING_EDIT,HIK-M-3,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_REAR_LINE,D1013,ST09,SLAVE,PLC_REQUEST/PC_REPLY,WORD,1,反面线扫双路采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),BLOCKED_CONFLICT,D1013与旧PLC清料别名重叠,FINS完整契约说明.md,PLC_MAPPING_EDIT,LINE-S-8,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-D1013
SIG_CAM_FRONT_LINE,D1014,ST10,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,正面线扫双路采图/完成握手,1=PLC请求;2=当前采图分支完成;3=失败(特殊码另核),LEGACY_STATIC_NEEDS_FIELD,D1014旧错位支路可能再写1,FINS完整契约说明.md,PLC_MAPPING_EDIT,LINE-M-2,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_CAM_REAR_LINE_CLEAR_ALIAS,D1013,MACHINE,PLC,DECLARATION_ONLY,WORD,1,PLC清料别名,可读PLC变量声明另作清料,BLOCKED_CONFLICT,与SIG_CAM_REAR_LINE重叠，核现场使用,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-D1013
SIG_TURNTABLE,D1015,TURN_TABLE,PLC,PLC_TO_PC,WORD,1,转盘号,0=未知/运动;1..8需核实际含义,PLC_SOURCE_ONLY,"不是CamDone,禁止批量清写",FINS完整契约说明.md,VIEW,,PLC_SOURCE_ONLY,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_FINAL_QUALITY,D1016,ST11,MASTER,PC_TO_PLC,WORD,1,整料最终质量,1=OK;2=NG,LEGACY_STATIC_NEEDS_FIELD,未判定需经工艺安全策略；真实出料口需核,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_LINE_REAR_SEQ,D1017,ST09,PLC,PLC_TO_PC,WORD,1,反面线扫相关计数,仅辅助，不是全机唯一料号,PLC_SOURCE_ONLY,值跨复位可能重复,FINS完整契约说明.md,VIEW,,PLC_SOURCE_ONLY,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_LINE_FRONT_SEQ,D1018,ST10,PLC,PLC_TO_PC,WORD,1,正面线扫相关计数,仅辅助，不是全机唯一料号,PLC_SOURCE_ONLY,值跨复位可能重复,FINS完整契约说明.md,VIEW,,PLC_SOURCE_ONLY,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_RESET_S,D1020,MACHINE,SLAVE,PLC_REQUEST/PC_REPLY,WORD,1,从机复位请求,1=请求;旧工程清0后本机reset,LEGACY_STATIC_NEEDS_FIELD,清0不代表机械或SDK已复位,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_RESET_M,D1021,MACHINE,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,主机复位请求,1=请求;旧工程清0后本机reset,LEGACY_STATIC_NEEDS_FIELD,必须协调主从epoch,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_YIELD_ALARM,D1022,MACHINE,PLC,PLC_TO_PC,WORD,1,良率报警(PLC声明),1=报警候选;系统规则须核,PLC_SOURCE_ONLY,不得当产品NG,FINS完整契约说明.md,VIEW,,PLC_SOURCE_ONLY,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_OPERATOR,D1023,MACHINE,MASTER,PC_TO_PLC,WORD,1,操作员数字等级与PLC同步,具体数字枚举需现场核,LEGACY_STATIC_NEEDS_FIELD,权限不能仅靠此数,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_READY_M,D1024,MACHINE,MASTER,PC_TO_PLC,WORD,1,主机准备就绪,1=本机准备完成,LEGACY_STATIC_NEEDS_FIELD,初始化可能清零，必须重新ready,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_READY_S,D1025,MACHINE,SLAVE,PC_TO_PLC,WORD,1,从机准备就绪,1=本机准备完成,LEGACY_STATIC_NEEDS_FIELD,主从须共同确认,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_OPERATOR_RESTORE,D1029,MACHINE,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,操作员权限恢复请求,1=恢复候选，清0不等于已恢复,LEGACY_STATIC_NEEDS_FIELD,权限审批和真实状态核对,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_BARCODE_REQUEST,D1999,LOADING,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,上料扫码上传握手,1=请求;2=成功;3=失败,LEGACY_STATIC_NEEDS_FIELD,旧程序2早于五路分发,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_BARCODE_WORDS,D2000..D2019,LOADING,MASTER,PLC_TO_PC,WORD_ARRAY,20,上料扫码文本原始窗口,旧PC读20WORD；旧PLC仅刷新10WORD,BLOCKED_CONFLICT,文本有效长度须现场逐字节对照,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-BARCODE
SIG_DOWNSTREAM_REQUEST,D2099,ST11,MASTER,PLC_REQUEST/PC_REPLY,WORD,1,末端扫码/上传结果握手,1=请求;2=完成;3=扫码上传失败;4=码错位,LEGACY_STATIC_NEEDS_FIELD,正常/失败顺序有区别,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_END_BARCODE_WORDS,D2100..D2119,ST10,MASTER,PLC_TO_PC,WORD_ARRAY,20,末端PLC扫码声明区,旧PC有读节点，但非已证实实际比较源,BLOCKED_CONFLICT,当前末端码来源需确认,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-BARCODE
SIG_DIM_M_L,D3010,MACHINE,MASTER,PC_TO_PLC,FLOAT32_BADC,2,主机产品长度,浮点实际值需读取;数值随产品方案,LEGACY_STATIC_NEEDS_FIELD,保存值与运行值可能不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_DIM_M_W,D3012,MACHINE,MASTER,PC_TO_PLC,FLOAT32_BADC,2,主机产品宽度,浮点实际值需读取;数值随产品方案,LEGACY_STATIC_NEEDS_FIELD,保存值与运行值可能不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_DIM_M_T,D3014,MACHINE,MASTER,PC_TO_PLC,FLOAT32_BADC,2,主机产品厚度,浮点实际值需读取;数值随产品方案,LEGACY_STATIC_NEEDS_FIELD,保存值与运行值可能不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_DIM_S_L,D3016,MACHINE,SLAVE,PC_TO_PLC,FLOAT32_BADC,2,从机产品长度,浮点实际值需读取;数值随产品方案,LEGACY_STATIC_NEEDS_FIELD,保存值与运行值可能不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_DIM_S_W,D3018,MACHINE,SLAVE,PC_TO_PLC,FLOAT32_BADC,2,从机产品宽度,浮点实际值需读取;数值随产品方案,LEGACY_STATIC_NEEDS_FIELD,保存值与运行值可能不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_DIM_S_T,D3020,MACHINE,SLAVE,PC_TO_PLC,FLOAT32_BADC,2,从机产品厚度,浮点实际值需读取;数值随产品方案,LEGACY_STATIC_NEEDS_FIELD,保存值与运行值可能不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_OLD_PLC_X,D1000,LOADING,PLC,PLC_READS,FLOAT32,2,旧PLC消费机器人X坐标,X,BLOCKED_CONFLICT,当前主机把D1000也用于线扫报警,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_OLD_PLC_Y,D1002,LOADING,PLC,PLC_READS,FLOAT32,2,旧PLC消费机器人Y坐标,Y,BLOCKED_CONFLICT,与新上位机源地址不同,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_OLD_PLC_U,D1004,LOADING,PLC,PLC_READS,FLOAT32,2,旧PLC消费机器人U角度,角度,BLOCKED_CONFLICT,单位和方向未核,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_OLD_UI_X,D1030,LOADING,MASTER,PC_TO_PLC,FLOAT32_BADC,2,旧主机写定位X坐标,X,BLOCKED_CONFLICT,旧PLC读D1000;禁止盲写,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_OLD_UI_Y,D1032,LOADING,MASTER,PC_TO_PLC,FLOAT32_BADC,2,旧主机写定位Y坐标,Y,BLOCKED_CONFLICT,旧PLC读D1002;禁止盲写,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_OLD_UI_U,D1034,LOADING,MASTER,PC_TO_PLC,FLOAT32_BADC,2,旧主机写定位U角度,角度,BLOCKED_CONFLICT,旧PLC读D1004;禁止盲写,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_M_LINE_ALARM_ALIAS,D1000,ST10,MASTER,PC_TO_PLC,WORD,1,主机线扫状态/计数报警旧写入,与D1000定位变量空间重叠,BLOCKED_CONFLICT,旧主机D1000/D1001用途与PLC坐标冲突,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_M_LINE_ALARM_ALIAS_2,D1001,ST10,MASTER,PC_TO_PLC,WORD,1,主机线扫状态/计数报警第二WORD,与D1000/1001浮点坐标重叠,BLOCKED_CONFLICT,不能猜测当前有效占用,FINS完整契约说明.md,PLC_MAPPING_EDIT,,BLOCKED_CONFLICT,,FALSE,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,G-XYU-1000
SIG_RECIPE_NAME,D3600..D3619,MACHINE,PLC,PLC_TO_PC,BA_STRING,20,旧主机自动换型配方标签,历史D3600字符串，实际有效编码须现场核,LEGACY_STATIC_NEEDS_FIELD,主机流程启用，从机旧换型流程禁用,FINS完整契约说明.md,PLC_MAPPING_EDIT,,STATIC_NEEDS_FIELD,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
SIG_POSITION_AUGMENT,D3500..D3505,LOADING,PLC,PLC_ONLY,FLOAT32_ARRAY,6,PLC与HMI定位补偿区,REAL数组等效WORD映射,PLC_SOURCE_ONLY,旧源码声明且与其他别名重叠,FINS完整契约说明.md,VIEW,,PLC_SOURCE_ONLY,,PENDING_FIELD_VERIFY,LEGACY_STATIC_NOT_FIELD,TRUE,FALSE,
""";

    private const string RawGates = """
GateId,Topic,Status,SourceConflict,RequiredEvidence,ProposedOwner,ProductionWriteAllowed,FieldSignoff
FG-01,定位XYU地址冲突,BLOCKED,D1030/D1000及线扫报警别名,现役PLC变量引用/坐标单位/只读读回+授权单站测试,EquipmentEngineer,FALSE,
FG-02,D1013线扫清料别名,BLOCKED,D1013 同时用于线扫完成/清料信号别名（旧 PLC 变量），现役版本未核,现役PLC代码/工艺清料现场流程,PLC Engineer,FALSE,
FG-03,条码20/40字节窗口,BLOCKED,原主机读20WORD PLC只写10WORD,抓raw WORD/规范化字符串案例,PLC Engineer,FALSE,
FG-04,末端核码源,BLOCKED,D2100存在读节点未证比较输出,原现场扫码程序/真实件对照,Quality Engineer,FALSE,
FG-05,D1016物理分流,BLOCKED,D1016=1/2 两个分支在旧PLC备份中均为普通放料，真实分流未核,真实OK/NG件投放确认,Process Engineer,FALSE,
FG-06,主从实际换型流程,BLOCKED,旧主启用从禁用,当前在线方案/新双机方案提交验证,System Engineer,FALSE,
FG-07,设备硬触发/用户集/方向,BLOCKED,只见SDK请求与保存vlcf,设备读回时序/真实FrameId,Vision Engineer,FALSE,
FG-08,PLC生产节拍与最终时间窗口,OPEN,旧PLC面阵10s线扫20s最终2s仅静态参考,现役 PLC Timer/真实延迟基准,Process Engineer,FALSE,
FG-09,线扫双光真实对应,OPEN,旧软件split1/split2非物理光源标识,标定靶及光路对应图像,Vision Engineer,FALSE,
FG-10,实际产品配方与主从尺寸,OPEN,旧保存主/从尺寸不同,现场实际型号主从参数/当前模型hash,Process Engineer,FALSE,
FG-11,相机SDK&板卡驱动兼容性,OPEN,旧路径和配置存在但未设备读回,Windows真机SDK/固件版本/兼容基线,Vision Engineer,FALSE,
FG-12,存储/MES/共享盘与保留策略,OPEN,旧本地数据库与Y/Z回退,数据库备份/存储峰值/MES合同,IT Quality,FALSE,
FG-13,UI 11结果图真实工艺正确性,OPEN,逻辑通道映射静态,每个实体相机实拍/方向/ROI/含义签字,Vision Engineer,FALSE,
FG-14,质量算法模型真值指标,OPEN,模型权重与真值集未完整核验,典型OK/NG/误检漏检集工艺阈值,Quality Engineer,FALSE,
""";
}
