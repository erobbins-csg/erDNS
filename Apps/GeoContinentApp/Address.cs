/*
Technitium DNS Server
Copyright (C) 2026  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.ApplicationCommon;
using MaxMind.GeoIP2.Responses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.EDnsOptions;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace GeoContinent
{
    public sealed class Address : IDnsApplication, IDnsAppRecordRequestHandler
    {
        #region variables

        internal readonly static JsonDocumentOptions _jsonParseOptions = new JsonDocumentOptions() { CommentHandling = JsonCommentHandling.Skip };

        IDnsServer? _dnsServer;
        MaxMind? _maxMind;

        static Dictionary<string, List<string>>? _groups;

        #endregion

        #region IDisposable

        bool _disposed;

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
                _maxMind?.Dispose();

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region internal

        internal static bool TryMatchGroup(JsonElement jsonAppRecordData, long? asn, string? continentCode, out JsonElement jsonContinent)
        {
            foreach (KeyValuePair<string, List<string>> group in _groups!)
            {
                if (jsonAppRecordData.TryGetProperty(group.Key, out jsonContinent))
                {
                    List<string> groupEntries = group.Value;

                    if (asn is not null)
                    {
                        string asnName = "AS" + asn;

                        foreach (string groupEntry in groupEntries)
                        {
                            if (groupEntry.Equals(asnName, StringComparison.Ordinal))
                                return true; //found ASN match
                        }
                    }

                    if (continentCode is not null)
                    {
                        foreach (string groupEntry in groupEntries)
                        {
                            if (groupEntry.Equals(continentCode, StringComparison.Ordinal))
                                return true; //found continent code match
                        }
                    }
                }
            }

            jsonContinent = default;
            return false;
        }

        #endregion

        #region public

        public async Task InitializeAsync(IDnsServer dnsServer, string? config)
        {
            _dnsServer = dnsServer;
            _maxMind = MaxMind.Create(dnsServer);

            if (string.IsNullOrWhiteSpace(config) || config.StartsWith('#') || !config.TrimStart().StartsWith('{'))
            {
                //save default config into file
                config = "{\r\n    \"groups\": {\r\n        \"custom-group\": [\r\n            \"AS\",\r\n            \"AF\",\r\n            \"AS1234\"\r\n        ]\r\n    }\r\n}\r\n";

                await File.WriteAllTextAsync(Path.Combine(dnsServer.ApplicationFolder, "dnsApp.config"), config);
            }

            using JsonDocument jsonDocument = JsonDocument.Parse(config, _jsonParseOptions);
            JsonElement jsonConfig = jsonDocument.RootElement;

            if (jsonConfig.TryGetProperty("groups", out JsonElement jsonGroups))
            {
                Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();

                foreach (JsonProperty jsonProperty in jsonGroups.EnumerateObject())
                {
                    string groupName = jsonProperty.Name;

                    JsonElement jsonGroupEntries = jsonProperty.Value;
                    if (jsonGroupEntries.ValueKind == JsonValueKind.Array)
                    {
                        List<string> groupEntries = new List<string>(jsonGroupEntries.GetArrayLength());

                        foreach (JsonElement jsonGroupEntry in jsonGroupEntries.EnumerateArray())
                        {
                            if (jsonGroupEntry.ValueKind == JsonValueKind.String)
                                groupEntries.Add(jsonGroupEntry.GetString()!);
                        }

                        groups.TryAdd(groupName, groupEntries);
                    }
                }

                _groups = groups;
            }
            else
            {
                _groups = new Dictionary<string, List<string>>();
            }
        }

        public Task<DnsDatagram?> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEP, DnsTransportProtocol protocol, bool isRecursionAllowed, string zoneName, string appRecordName, uint appRecordTtl, string appRecordData)
        {
            DnsQuestionRecord question = request.Question[0];

            if (!question.Name.Equals(appRecordName, StringComparison.OrdinalIgnoreCase) && !appRecordName.StartsWith('*'))
                return Task.FromResult<DnsDatagram?>(null);

            switch (question.Type)
            {
                case DnsResourceRecordType.A:
                case DnsResourceRecordType.AAAA:
                    if (_maxMind is null)
                        throw new InvalidOperationException("MaxMind database not initialized.");

                    using (JsonDocument jsonDocument = JsonDocument.Parse(appRecordData, _jsonParseOptions))
                    {
                        JsonElement jsonAppRecordData = jsonDocument.RootElement;
                        JsonElement jsonContinent = default;

                        byte scopePrefixLength = 0;
                        EDnsClientSubnetOptionData requestECS = request.GetEDnsClientSubnetOption();
                        if (requestECS is not null)
                        {
                            long? asn = null;

                            if ((_maxMind.IspReader is not null) && _maxMind.IspReader.TryIsp(requestECS.Address, out IspResponse? csIsp) && (csIsp.Network is not null))
                            {
                                scopePrefixLength = (byte)csIsp.Network.PrefixLength;
                                asn = csIsp.AutonomousSystemNumber;
                            }
                            else if ((_maxMind.AsnReader is not null) && _maxMind.AsnReader.TryAsn(requestECS.Address, out AsnResponse? csAsn) && (csAsn.Network is not null))
                            {
                                scopePrefixLength = (byte)csAsn.Network.PrefixLength;
                                asn = csAsn.AutonomousSystemNumber;
                            }
                            else
                            {
                                scopePrefixLength = requestECS.SourcePrefixLength;
                            }

                            if ((asn is null) || !jsonAppRecordData.TryGetProperty("AS" + asn, out jsonContinent))
                            {
                                if (_maxMind.CountryReader.TryCountry(requestECS.Address, out CountryResponse? csResponse) && (csResponse.Continent.Code is not null))
                                {
                                    if (!jsonAppRecordData.TryGetProperty(csResponse.Continent.Code, out jsonContinent))
                                        if (!TryMatchGroup(jsonAppRecordData, asn, csResponse.Continent.Code, out jsonContinent))
                                            jsonAppRecordData.TryGetProperty("default", out jsonContinent);
                                }
                                else if (asn is not null)
                                {
                                    if (!TryMatchGroup(jsonAppRecordData, asn, null, out jsonContinent))
                                        jsonAppRecordData.TryGetProperty("default", out jsonContinent);
                                }
                            }
                        }

                        if (jsonContinent.ValueKind == JsonValueKind.Undefined)
                        {
                            long? asn = null;

                            if ((_maxMind.IspReader is not null) && _maxMind.IspReader.TryIsp(remoteEP.Address, out IspResponse? csIsp) && (csIsp.Network is not null))
                                asn = csIsp.AutonomousSystemNumber;
                            else if ((_maxMind.AsnReader is not null) && _maxMind.AsnReader.TryAsn(remoteEP.Address, out AsnResponse? csAsn) && (csAsn.Network is not null))
                                asn = csAsn.AutonomousSystemNumber;

                            if ((asn is null) || !jsonAppRecordData.TryGetProperty("AS" + asn, out jsonContinent))
                            {
                                if (_maxMind.CountryReader.TryCountry(remoteEP.Address, out CountryResponse? response) && (response.Continent.Code is not null))
                                {
                                    if (!jsonAppRecordData.TryGetProperty(response.Continent.Code, out jsonContinent))
                                        if (!TryMatchGroup(jsonAppRecordData, asn, response.Continent.Code, out jsonContinent))
                                            if (!jsonAppRecordData.TryGetProperty("default", out jsonContinent))
                                                return Task.FromResult<DnsDatagram?>(null);
                                }
                                else if (asn is not null)
                                {
                                    if (!TryMatchGroup(jsonAppRecordData, asn, null, out jsonContinent))
                                        if (!jsonAppRecordData.TryGetProperty("default", out jsonContinent))
                                            return Task.FromResult<DnsDatagram?>(null);
                                }
                                else
                                {
                                    if (!jsonAppRecordData.TryGetProperty("default", out jsonContinent))
                                        return Task.FromResult<DnsDatagram?>(null);
                                }
                            }
                        }

                        List<DnsResourceRecord> answers = new List<DnsResourceRecord>();

                        switch (question.Type)
                        {
                            case DnsResourceRecordType.A:
                                foreach (JsonElement jsonAddress in jsonContinent.EnumerateArray())
                                {
                                    if (jsonAddress.ValueKind != JsonValueKind.String)
                                        continue;

                                    IPAddress address = IPAddress.Parse(jsonAddress.GetString()!);

                                    if (address.AddressFamily == AddressFamily.InterNetwork)
                                        answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.A, DnsClass.IN, appRecordTtl, new DnsARecordData(address)));
                                }
                                break;

                            case DnsResourceRecordType.AAAA:
                                foreach (JsonElement jsonAddress in jsonContinent.EnumerateArray())
                                {
                                    if (jsonAddress.ValueKind != JsonValueKind.String)
                                        continue;

                                    IPAddress address = IPAddress.Parse(jsonAddress.GetString()!);

                                    if (address.AddressFamily == AddressFamily.InterNetworkV6)
                                        answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.AAAA, DnsClass.IN, appRecordTtl, new DnsAAAARecordData(address)));
                                }
                                break;
                        }

                        if (answers.Count == 0)
                            return Task.FromResult<DnsDatagram?>(null);

                        if (answers.Count > 1)
                            answers.Shuffle();

                        EDnsOption[]? options = null;

                        if (requestECS is not null)
                            options = EDnsClientSubnetOptionData.GetEDnsClientSubnetOption(requestECS.SourcePrefixLength, scopePrefixLength, requestECS.Address);

                        return Task.FromResult<DnsDatagram?>(new DnsDatagram(request.Identifier, true, request.OPCODE, true, false, request.RecursionDesired, isRecursionAllowed, false, false, DnsResponseCode.NoError, request.Question, answers, null, null, _dnsServer!.UdpPayloadSize, EDnsHeaderFlags.None, options));
                    }

                default:
                    return Task.FromResult<DnsDatagram?>(null);
            }
        }

        #endregion

        #region properties

        public string Description
        { get { return "Returns A or AAAA records based on the continent or Autonomous System Number (ASN) the client queries from using MaxMind GeoIP2 Country database. Use the two character continent code like \"NA\" (North America) or \"OC\" (Oceania) or ASN like 'AS1234'. Note that ASN will be matched before the continent code."; } }

        public string ApplicationRecordDataTemplate
        {
            get
            {
                return @"{
  ""EU"": [
    ""1.1.1.1"", 
    ""2.2.2.2""
  ],
  ""AS1234"": [
    ""3.3.3.3""
  ],
  ""custom-group"": [
    ""4.4.4.4""
  ],
  ""default"": [
    ""5.5.5.5""
  ]
}";
            }
        }

        #endregion
    }
}
