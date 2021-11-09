﻿using Application.Common.Interfaces;
using Application.Options;
using Application.VaccineCredential.Queries.GetVaccineCredential;
using Application.VaccineCredential.Queries.GetVaccineStatus;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.AzureSynapse
{
    public class AzureSynapseService : IAzureSynapseService
    {
        private readonly ILogger<AzureSynapseService> _logger;
        private readonly AzureSynapseSettings _azureSynapseSettings;

        #region Constructor

        public AzureSynapseService(ILogger<AzureSynapseService> logger, AzureSynapseSettings azureSynapseSettings)
        {
            _logger = logger;
            _azureSynapseSettings = azureSynapseSettings;
        }

        #endregion Constructor

        private class VcObject
        {
            public Vc Vc { get; set; }
        }

        #region IAzureSynapseService Implementation

        public async Task<Vc> GetVaccineCredentialSubjectAsync(string id, string requestId, CancellationToken cancellationToken)
        {
            Vc vaccineCredential = null;

            using (var conn = new SqlConnection())
            {
                _logger.LogInformation($"CONNECTING: Trying to connect to Azure Synapse Analytics Database; request.Id={requestId}");

                conn.ConnectionString = _azureSynapseSettings.ConnectionString;

                var cmdVc = conn.CreateCommand();
                cmdVc.CommandText = _azureSynapseSettings.IdQuery;
                cmdVc.CommandType = System.Data.CommandType.StoredProcedure;
                cmdVc.Parameters.AddWithValue("@UserId", id);

                conn.Open();
                _logger.LogInformation($"CALLING: {_azureSynapseSettings.IdQuery}; request.Id={requestId}");
                var rdVc = await cmdVc.ExecuteReaderAsync(cancellationToken);

                if (await rdVc.ReadAsync(cancellationToken))
                {
                    var jsonString = rdVc.GetString(0);
                    var vaccineCredentialobject = JsonConvert.DeserializeObject<VcObject>(jsonString);
                    vaccineCredential = vaccineCredentialobject.Vc;
                }
            }

            return vaccineCredential;
        }

        public async Task<string> GetVaccineCredentialStatusAsync(GetVaccineCredentialStatusQuery request, CancellationToken cancellationToken)
        {
            string Guid = null;

            using (var conn = new SqlConnection())
            {
                _logger.LogInformation($"CONNECTING: Trying to connect to Azure Synapse Analytics Database; request.Id={request.Id}");

                conn.ConnectionString = _azureSynapseSettings.ConnectionString;

                var cmdVc = CreateCommand(conn, request, _azureSynapseSettings.StatusQuery);

                conn.Open();

                _logger.LogInformation($"CALLING: {_azureSynapseSettings.StatusQuery}; request.Id={request.Id}");
                var rdVc = await cmdVc.ExecuteScalarAsync(cancellationToken);

                if (rdVc != null)
                {
                    Guid = Convert.ToString(rdVc);
                }

                if (string.IsNullOrWhiteSpace(Guid) && _azureSynapseSettings.UseRelaxed == "1")
                {
                    //prepare for call to relaxed...
                    cmdVc = CreateCommand(conn, request, _azureSynapseSettings.RelaxedQuery);
                    _logger.LogInformation($"CALLING: {_azureSynapseSettings.RelaxedQuery}; request.Id={request.Id}");
                    rdVc = await cmdVc.ExecuteScalarAsync(cancellationToken);
                    if (rdVc != null)
                    {
                        Guid = Convert.ToString(rdVc);
                    }
                }
            }

            return Guid;
        }

        #endregion IAzureSynapseService Implementation

        private static SqlCommand CreateCommand(SqlConnection conn, GetVaccineCredentialStatusQuery request, string query)
        {
            var cmdVc = conn.CreateCommand();
            cmdVc.CommandText = query;
            cmdVc.CommandType = System.Data.CommandType.StoredProcedure;
            cmdVc.Parameters.AddWithValue("@FirstName", request.FirstName.ToUpper().Trim());
            cmdVc.Parameters.AddWithValue("@LastName", request.LastName.ToUpper().Trim());
            cmdVc.Parameters.AddWithValue("@DateOfBirth", request.DateOfBirth?.ToString("yyyy-MM-dd"));
            cmdVc.Parameters.AddWithValue("@PhoneNumber", !string.IsNullOrWhiteSpace(request.PhoneNumber.Trim()) ? request.PhoneNumber.Trim() : "");
            cmdVc.Parameters.AddWithValue("@EmailAddress", !string.IsNullOrWhiteSpace(request.EmailAddress.ToLower().Trim()) ? request.EmailAddress.ToLower().Trim() : "");
            return cmdVc;
        }
    }
}