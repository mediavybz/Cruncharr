#nullable disable
#pragma warning disable CS8632
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Xml;
using Cruncharr.Core.Utils.Parser.Utils;

namespace Cruncharr.Core.Utils.Parser;

public class InheritAttributes
{
    public static Dictionary<string, string> KeySystemsMap = new Dictionary<string, string>{
        { "urn:uuid:1077efec-c0b2-4d02-ace3-3c1e52e2fb4b", "org.w3.clearkey" },
        { "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", "com.widevine.alpha" },
        { "urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95", "com.microsoft.playready" },
        { "urn:uuid:f239e769-efa3-4850-9c16-a903c6932efb", "com.adobe.primetime" },
        { "urn:mpeg:dash:mp4protection:2011", "mp4protection" }
    };

    public static dynamic GenerateKeySystemInformation(List<XmlElement> contentProtectionNodes)
    {
        var keySystemInfo = new ExpandoObject() as IDictionary<string, object>;

        foreach (var node in contentProtectionNodes)
        {
            dynamic attributes = ParseAttribute.ParseAttributes(node);
            var attributesDict = attributes as IDictionary<string, object>;

            if (attributesDict == null)
                continue;

            if (!attributesDict.TryGetValue("schemeIdUri", out var schemeObj))
                continue;

            var schemeIdUri = schemeObj?.ToString()?.ToLower();

            if (schemeIdUri == null)
                continue;

            attributesDict["schemeIdUri"] = schemeIdUri;

            if (!KeySystemsMap.TryGetValue(schemeIdUri, out var keySystem))
                continue;

            dynamic info = new ExpandoObject();
            info.attributes = attributes;

            var psshNode = XMLUtils.FindChildren(node, "cenc:pssh").FirstOrDefault();

            if (psshNode != null)
            {
                string pssh = psshNode.InnerText;

                if (!string.IsNullOrEmpty(pssh))
                    info.pssh = DecodeB64ToUint8Array(pssh);
            }

            keySystemInfo[keySystem] = info;
        }

        return keySystemInfo;
    }

    private static byte[] DecodeB64ToUint8Array(string base64String)
    {
        return Convert.FromBase64String(base64String);
    }

    public static string GetContent(XmlElement element) => element.InnerText.Trim();

    public static List<dynamic> BuildBaseUrls(List<dynamic> references, List<XmlElement> baseUrlElements)
    {
        if (!baseUrlElements.Any())
        {
            return references;
        }

        return references.SelectMany(reference =>
            baseUrlElements.Select(baseUrlElement =>
            {
                var initialBaseUrl = GetContent(baseUrlElement);
                string resolvedBaseUrl = UrlUtils.ResolveUrl(reference.baseUrl, initialBaseUrl);

                dynamic finalBaseUrl = new ExpandoObject();
                finalBaseUrl.baseUrl = resolvedBaseUrl;

                ObjectUtilities.MergeExpandoObjects(finalBaseUrl, ParseAttribute.ParseAttributes(baseUrlElement));

                if (resolvedBaseUrl != initialBaseUrl && finalBaseUrl.serviceLocation == null && reference.serviceLocation != null)
                {
                    finalBaseUrl.ServiceLocation = reference.ServiceLocation;
                }

                return finalBaseUrl;
            })
        ).ToList();
    }

    public static double? GetPeriodStart(dynamic attributes, dynamic? priorPeriodAttributes, string mpdType)
    {
        var attributesL = attributes as IDictionary<string, object>;
        if (attributesL != null && attributesL.ContainsKey("start") && (attributesL["start"] is double || attributesL["start"] is long || attributesL["start"] is float || attributesL["start"] is int))
        {
            return (double)attributes.start;
        }

        var priorPeriodAttributesL = priorPeriodAttributes as IDictionary<string, object>;
        if (priorPeriodAttributesL != null && priorPeriodAttributesL.ContainsKey("start") && priorPeriodAttributesL.ContainsKey("duration") &&
            (priorPeriodAttributesL["start"] is double || priorPeriodAttributesL["start"] is long || priorPeriodAttributesL["start"] is float || priorPeriodAttributesL["start"] is int) &&
            (priorPeriodAttributesL["duration"] is double || priorPeriodAttributesL["duration"] is long || priorPeriodAttributesL["duration"] is float || priorPeriodAttributesL["duration"] is int))
        {
            return (double)priorPeriodAttributes.start + (double)priorPeriodAttributes.duration;
        }

        if (priorPeriodAttributesL == null && string.Equals(mpdType, "static", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return null;
    }

    public class ContentSteeringInfo
    {
        public string ServerURL { get; set; } = "";
        public bool QueryBeforeStart { get; set; }
    }

    public static ContentSteeringInfo? GenerateContentSteeringInformation(List<XmlElement> contentSteeringNodes)
    {
        if (contentSteeringNodes.Count > 1)
        {
            Console.WriteLine("The MPD manifest should contain no more than one ContentSteering tag");
        }

        if (contentSteeringNodes.Count == 0)
        {
            return null;
        }

        XmlElement firstContentSteeringNode = contentSteeringNodes[0];
        ContentSteeringInfo infoFromContentSteeringTag = new ContentSteeringInfo
        {
            ServerURL = XMLUtils.GetContent(firstContentSteeringNode),
            QueryBeforeStart = Convert.ToBoolean(firstContentSteeringNode.GetAttribute("queryBeforeStart"))
        };

        return infoFromContentSteeringTag;
    }

    private static dynamic CreateExpandoWithTag(string tag)
    {
        dynamic expando = new ExpandoObject();
        expando.tag = tag;
        return expando;
    }

    public static dynamic GetSegmentInformation(XmlElement adaptationSet)
    {
        dynamic segmentInfo = new ExpandoObject();

        var segmentTemplate = XMLUtils.FindChildren(adaptationSet, "SegmentTemplate").FirstOrDefault();
        var segmentList = XMLUtils.FindChildren(adaptationSet, "SegmentList").FirstOrDefault();
        var segmentUrls = segmentList != null
            ? XMLUtils.FindChildren(segmentList, "SegmentURL").Select(s => ObjectUtilities.MergeExpandoObjects(CreateExpandoWithTag("SegmentURL"), ParseAttribute.ParseAttributes(s))).ToList()
            : null;
        var segmentBase = XMLUtils.FindChildren(adaptationSet, "SegmentBase").FirstOrDefault();
        var segmentTimelineParentNode = segmentList ?? segmentTemplate;
        var segmentTimeline = segmentTimelineParentNode != null ? XMLUtils.FindChildren(segmentTimelineParentNode, "SegmentTimeline").FirstOrDefault() : null;
        var segmentInitializationParentNode = segmentList ?? segmentBase ?? segmentTemplate;
        var segmentInitialization = segmentInitializationParentNode != null ? XMLUtils.FindChildren(segmentInitializationParentNode, "Initialization").FirstOrDefault() : null;

        dynamic? template = segmentTemplate != null ? ParseAttribute.ParseAttributes(segmentTemplate) : null;

        if (template != null && segmentInitialization != null)
        {
            template.initialization = ParseAttribute.ParseAttributes(segmentInitialization);
        }
        else if (template != null && template.initialization != null)
        {
            dynamic init = new ExpandoObject();
            init.sourceURL = template.initialization;
            template.initialization = init;
        }

        segmentInfo.template = template;
        segmentInfo.segmentTimeline = segmentTimeline != null ? XMLUtils.FindChildren(segmentTimeline, "S").Select(s => ParseAttribute.ParseAttributes(s)).ToList() : null;
        segmentInfo.list = segmentList != null
            ? ObjectUtilities.MergeExpandoObjects(ParseAttribute.ParseAttributes(segmentList), new { segmentUrls, initialization = ParseAttribute.ParseAttributes(segmentInitialization) })
            : null;

        dynamic initBas = new ExpandoObject();
        initBas.initialization = ParseAttribute.ParseAttributes(segmentInitialization);
        segmentInfo.@base = segmentBase != null ? ObjectUtilities.MergeExpandoObjects(ParseAttribute.ParseAttributes(segmentBase), initBas) : null;

        var dict = (IDictionary<string, object?>)segmentInfo;
        var keys = dict.Keys.ToList();
        foreach (var key in keys)
        {
            if (dict[key] == null)
            {
                dict.Remove(key);
            }
        }

        return segmentInfo;
    }

    public static List<dynamic> ParseCaptionServiceMetadata(dynamic service)
    {
        List<dynamic> parsedMetadata = new List<dynamic>();

        var tempTestService = service as IDictionary<string, Object>;

        if (tempTestService == null || !tempTestService.ContainsKey("schemeIdUri"))
        {
            return parsedMetadata;
        }

        if (service.schemeIdUri == "urn:scte:dash:cc:cea-608:2015")
        {
            var values = service.value is string ? service.value.Split(';') : new string[0];

            foreach (var value in values)
            {
                dynamic metadata = new ExpandoObject();
                string? channel = null;
                string language = value;

                if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^CC\d="))
                {
                    var parts = value.Split('=');
                    channel = parts[0];
                    language = parts[1];
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^CC\d$"))
                {
                    channel = value;
                }

                metadata.channel = channel;
                metadata.language = language;

                parsedMetadata.Add(metadata);
            }
        }
        else if (service.schemeIdUri == "urn:scte:dash:cc:cea-708:2015")
        {
            var values = service.value is string ? service.value.Split(';') : new string[0];

            foreach (var value in values)
            {
                dynamic metadata = new ExpandoObject();
                metadata.channel = default(string);
                metadata.language = default(string);
                metadata.aspectRatio = 1;
                metadata.easyReader = 0;
                metadata._3D = 0;

                if (value.Contains("="))
                {
                    var parts = value.Split('=');
                    var channel = parts[0];
                    var opts = parts.Length > 1 ? parts[1] : "";

                    metadata.channel = "SERVICE" + channel;
                    metadata.language = value;

                    var options = opts.Split(',');
                    foreach (var opt in options)
                    {
                        var optionParts = opt.Split(':');
                        var name = optionParts[0];
                        var val = optionParts.Length > 1 ? optionParts[1] : "";

                        switch (name)
                        {
                            case "lang":
                                metadata.language = val;
                                break;
                            case "er":
                                metadata.easyReader = Convert.ToInt32(val);
                                break;
                            case "war":
                                metadata.aspectRatio = Convert.ToInt32(val);
                                break;
                            case "3D":
                                metadata._3D = Convert.ToInt32(val);
                                break;
                        }
                    }
                }
                else
                {
                    metadata.language = value;
                }

                parsedMetadata.Add(metadata);
            }
        }

        return parsedMetadata;
    }

    public static List<ExpandoObject> ToRepresentations(dynamic periodAttributes, dynamic periodBaseUrls, dynamic periodSegmentInfo, XmlElement adaptationSet)
    {
        dynamic adaptationSetAttributes = ParseAttribute.ParseAttributes(adaptationSet);
        var adaptationSetBaseUrls = BuildBaseUrls(periodBaseUrls, XMLUtils.FindChildren(adaptationSet, "BaseURL"));
        var role = XMLUtils.FindChildren(adaptationSet, "Role").FirstOrDefault();
        dynamic roleAttributes = new ExpandoObject();
        roleAttributes.role = ParseAttribute.ParseAttributes(role);

        dynamic attrs = ObjectUtilities.MergeExpandoObjects(periodAttributes, adaptationSetAttributes);
        attrs = ObjectUtilities.MergeExpandoObjects(attrs, roleAttributes);

        var accessibility = XMLUtils.FindChildren(adaptationSet, "Accessibility").FirstOrDefault();
        var captionServices = ParseCaptionServiceMetadata(accessibility != null ? ParseAttribute.ParseAttributes(accessibility) : null);

        if (captionServices != null)
        {
            attrs = ObjectUtilities.MergeExpandoObjects(attrs, new { captionServices });
        }

        XmlElement? label = XMLUtils.FindChildren(adaptationSet, "Label").FirstOrDefault();
        if (label is { ChildNodes.Count: > 0 })
        {
            var labelVal = label.ChildNodes[0]?.Value?.Trim();
            attrs = ObjectUtilities.MergeExpandoObjects(attrs, new { label = labelVal });
        }

        var nodes = XMLUtils.FindChildren(adaptationSet, "ContentProtection");
        var contentProtection = GenerateKeySystemInformation(nodes);
        var tempTestContentProtection = contentProtection as IDictionary<string, Object>;
        if (tempTestContentProtection is { Count: > 0 })
        {
            dynamic contentProt = new ExpandoObject();
            contentProt.contentProtection = contentProtection;
            attrs = ObjectUtilities.MergeExpandoObjects(attrs, contentProt);
        }

        var segmentInfo = GetSegmentInformation(adaptationSet);
        var representations = XMLUtils.FindChildren(adaptationSet, "Representation");
        var adaptationSetSegmentInfo = ObjectUtilities.MergeExpandoObjects(periodSegmentInfo, segmentInfo);

        List<ExpandoObject> list = new List<ExpandoObject>();
        for (int i = 0; i < representations.Count; i++)
        {
            List<dynamic> res = InheritBaseUrls(attrs, adaptationSetBaseUrls, adaptationSetSegmentInfo, representations[i]);
            foreach (dynamic re in res)
            {
                list.Add(re);
            }
        }

        return list;
    }

    public static List<dynamic> InheritBaseUrls(dynamic adaptationSetAttributes, dynamic adaptationSetBaseUrls, dynamic adaptationSetSegmentInfo, XmlElement representation)
    {
        var repBaseUrlElements = XMLUtils.FindChildren(representation, "BaseURL");
        List<dynamic> repBaseUrls = BuildBaseUrls(adaptationSetBaseUrls, repBaseUrlElements);
        var attributes = ObjectUtilities.MergeExpandoObjects(adaptationSetAttributes, ParseAttribute.ParseAttributes(representation));

        var repProtectionNodes = XMLUtils.FindChildren(representation, "ContentProtection");
        var repContentProtection = GenerateKeySystemInformation(repProtectionNodes);

        if ((repContentProtection as IDictionary<string, object>)?.Count > 0)
        {
            dynamic contentProt = new ExpandoObject();
            contentProt.contentProtection = repContentProtection;
            attributes = ObjectUtilities.MergeExpandoObjects(attributes, contentProt);
        }

        var representationSegmentInfo = GetSegmentInformation(representation);

        return repBaseUrls.Select(baseUrl =>
        {
            dynamic result = new ExpandoObject();
            result.segmentInfo = ObjectUtilities.MergeExpandoObjects(adaptationSetSegmentInfo, representationSegmentInfo);
            result.attributes = ObjectUtilities.MergeExpandoObjects(attributes, baseUrl);
            return result;
        }).ToList();
    }

    private static List<ExpandoObject> ToAdaptationSets(ExpandoObject mpdAttributes, dynamic mpdBaseUrls, dynamic period, int index)
    {
        dynamic periodBaseUrls = BuildBaseUrls(mpdBaseUrls, XMLUtils.FindChildren(period.node, "BaseURL"));
        dynamic start = new ExpandoObject();
        start.periodStart = period.attributes.start;
        dynamic periodAttributes = ObjectUtilities.MergeExpandoObjects(mpdAttributes, start);

        var tempTestAttributes = period.attributes as IDictionary<string, Object>;
        if (tempTestAttributes != null && tempTestAttributes.ContainsKey("duration") &&
            (tempTestAttributes["duration"] is double || tempTestAttributes["duration"] is long || tempTestAttributes["duration"] is float || tempTestAttributes["duration"] is int))
        {
            periodAttributes.periodDuration = period.attributes.duration;
        }

        List<XmlElement> adaptationSets = XMLUtils.FindChildren(period.node, "AdaptationSet");
        dynamic periodSegmentInfo = GetSegmentInformation(period.node);

        List<ExpandoObject> list = new List<ExpandoObject>();

        for (int i = 0; i < adaptationSets.Count; i++)
        {
            List<ExpandoObject> res = ToRepresentations(periodAttributes, periodBaseUrls, periodSegmentInfo, adaptationSets[i]);
            foreach (dynamic re in res)
            {
                list.Add(re);
            }
        }

        return list;
    }

    public static ManifestInfo InheritAttributesFun(XmlElement mpd, Dictionary<string, object>? options = null)
    {
        if (options == null)
            options = new Dictionary<string, object>();

        string manifestUri = options.ContainsKey("manifestUri") ? (string)options["manifestUri"] : string.Empty;
        long NOW = options.ContainsKey("NOW") ? (long)options["NOW"] : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int clientOffset = options.ContainsKey("clientOffset") ? (int)options["clientOffset"] : 0;
        Action eventHandler = options.ContainsKey("eventHandler") ? (Action)options["eventHandler"] : () => { };

        List<XmlElement> periodNodes = XMLUtils.FindChildren(mpd, "Period");

        if (periodNodes.Count == 0)
        {
            throw new Exception(Errors.INVALID_NUMBER_OF_PERIOD);
        }

        List<XmlElement> locations = XMLUtils.FindChildren(mpd, "Location");
        dynamic mpdAttributes = ParseAttribute.ParseAttributes(mpd);
        dynamic baseUrl = new ExpandoObject();
        baseUrl.baseUrl = manifestUri;
        dynamic mpdBaseUrls = BuildBaseUrls(new List<dynamic> { baseUrl }, XMLUtils.FindChildren(mpd, "BaseUrl"));
        List<XmlElement> contentSteeringNodes = XMLUtils.FindChildren(mpd, "ContentSteering");

        ObjectUtilities.SetAttributeWithDefault(mpdAttributes, "type", "static");
        ObjectUtilities.SetFieldFromOrToDefault(mpdAttributes, "sourceDuration", "mediaPresentationDuration", 0);
        mpdAttributes.NOW = NOW;
        mpdAttributes.clientOffset = clientOffset;

        if (locations.Count > 0)
        {
            mpdAttributes.locations = locations.Cast<XmlElement>().Select(location => location.InnerText).ToList();
        }

        List<ExpandoObject> periods = new List<ExpandoObject>();

        for (int i = 0; i < periodNodes.Count; i++)
        {
            XmlElement periodNode = periodNodes[i];
            dynamic attributes = ParseAttribute.ParseAttributes(periodNode);

            int getIndex = i - 1;

            dynamic? priorPeriod = null;
            if (getIndex >= 0 && getIndex < periods.Count)
            {
                priorPeriod = periods[getIndex];
            }

            attributes.start = GetPeriodStart(attributes, priorPeriod, mpdAttributes.type);

            dynamic finalPeriod = new ExpandoObject();
            finalPeriod.node = periodNode;
            finalPeriod.attributes = attributes;

            periods.Add(finalPeriod);
        }

        List<ExpandoObject> representationInfo = new List<ExpandoObject>();

        for (int i = 0; i < periods.Count; i++)
        {
            List<ExpandoObject> result = ToAdaptationSets(mpdAttributes, mpdBaseUrls, periods[i], i);
            foreach (dynamic re in result)
            {
                representationInfo.Add(re);
            }
        }

        return new ManifestInfo
        {
            locations = ObjectUtilities.GetAttributeWithDefault(mpdAttributes, "locations", null),
            contentSteeringInfo = GenerateContentSteeringInformation(contentSteeringNodes.Cast<XmlElement>().ToList()),
            representationInfo = representationInfo,
        };
    }
}
