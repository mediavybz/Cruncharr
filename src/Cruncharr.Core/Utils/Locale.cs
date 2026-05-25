using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Cruncharr.Core.Utils;

[DataContract]
[JsonConverter(typeof(LocaleConverter))]
public enum Locale{
    [EnumMember(Value = "")]
    DefaulT,
    
    [EnumMember(Value = "un")]
    Unknown,

    [EnumMember(Value = "en-US")]
    EnUs,

    [EnumMember(Value = "es-LA")]
    EsLa,

    [EnumMember(Value = "es-419")]
    Es419,

    [EnumMember(Value = "es-ES")]
    EsEs,

    [EnumMember(Value = "pt-BR")]
    PtBr,

    [EnumMember(Value = "fr-FR")]
    FrFr,

    [EnumMember(Value = "de-DE")]
    DeDe,

    [EnumMember(Value = "ar-ME")]
    ArMe,

    [EnumMember(Value = "ar-SA")]
    ArSa,

    [EnumMember(Value = "it-IT")]
    ItIt,

    [EnumMember(Value = "ru-RU")]
    RuRu,

    [EnumMember(Value = "tr-TR")]
    TrTr,

    [EnumMember(Value = "hi-IN")]
    HiIn,

    [EnumMember(Value = "te-IN")]
    TeIn,

    [EnumMember(Value = "ta-IN")]
    TaIn,

    [EnumMember(Value = "zh-CN")]
    ZhCn,

    [EnumMember(Value = "zh-HK")]
    ZhHk,

    [EnumMember(Value = "zh-TW")]
    ZhTw,

    [EnumMember(Value = "ms-MY")]
    MsMy,

    [EnumMember(Value = "id-ID")]
    IdId,

    [EnumMember(Value = "th-TH")]
    ThTh,

    [EnumMember(Value = "vi-VN")]
    ViVn,

    [EnumMember(Value = "pl-PL")]
    PlPl,

    [EnumMember(Value = "ca-ES")]
    CaEs,

    [EnumMember(Value = "ko-KR")]
    KoKr,

    [EnumMember(Value = "ja-JP")]
    JaJp,

    [EnumMember(Value = "pt-PT")]
    PtPt,

    [EnumMember(Value = "en-IN")]
    EnIn
}
