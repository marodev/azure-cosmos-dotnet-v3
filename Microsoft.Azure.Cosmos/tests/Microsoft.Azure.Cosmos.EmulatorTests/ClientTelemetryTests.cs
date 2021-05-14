﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.SDK.EmulatorTests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using HdrHistogram;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.CosmosElements.Telemetry;
    using Microsoft.Azure.Cosmos.Fluent;
    using Microsoft.Azure.Documents;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using System.Threading;
    using System.Net;
    using System.Linq;
    using Newtonsoft.Json.Linq;
    using System.Net.Http;
    using Moq.Protected;
    using Moq;
    using Newtonsoft.Json;

    [TestClass]
    public class ClientTelemetryTests : BaseCosmosClientHelper
    {
        private Container container;
        private List<string> allowedMetrics;
        private List<string> allowedUnitnames;

        private ClientTelemetryInfo telemetryInfo;

        [TestInitialize]
        public async Task TestInitialize()
        {
            Environment.SetEnvironmentVariable(ClientTelemetryOptions.EnvPropsClientTelemetrySchedulingInSeconds, "1");
            Environment.SetEnvironmentVariable(ClientTelemetryOptions.EnvPropsClientTelemetryVmMetadataUrl, "http://8gl6e.mocklab.io/metadata"); 
            CosmosClientBuilder cosmosClientBuilder = TestCommon.GetDefaultConfiguration();
            cosmosClientBuilder
                .WithTelemetryEnabled();

            this.cosmosClient = cosmosClientBuilder.Build();

            this.database = await this.cosmosClient.CreateDatabaseAsync(Guid.NewGuid().ToString());
            this.container = await this.database.CreateContainerAsync(Guid.NewGuid().ToString(), "/id");

            this.telemetryInfo = this.cosmosClient.Telemetry.ClientTelemetryInfo;

            this.allowedMetrics = new List<string>(new string[] {
                ClientTelemetryOptions.RequestChargeName,
                ClientTelemetryOptions.RequestLatencyName
            });

            this.allowedUnitnames = new List<string>(new string[] {
                ClientTelemetryOptions.RequestChargeUnit,
                ClientTelemetryOptions.RequestLatencyUnit
            });
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            await base.TestCleanup();
        }

        [TestMethod]
        public async Task PointSuccessOperationsTest()
        {
            // Create an item
            var testItem = new { id = "MyTestItemId", partitionKeyPath = "MyTestPkValue", details = "it's working", status = "done" };
            ItemResponse<dynamic> createResponse = await this.container.CreateItemAsync<dynamic>(testItem);
            dynamic testItemCreated = createResponse.Resource;
            List<Documents.OperationType> allowedOperations
               = new List<Documents.OperationType>(new Documents.OperationType[] {
                                Documents.OperationType.Create
               });
            IDictionary<OperationType, HttpStatusCode> expectedOperationCodeMap
               = new Dictionary<OperationType, HttpStatusCode>
               {
                    { OperationType.Create, HttpStatusCode.Created }
               };
            this.AssertClientTelemetryInfo(allowedOperations, 2, expectedOperationCodeMap);

            // Read an Item
            await this.container.ReadItemAsync<dynamic>(testItem.id, new Cosmos.PartitionKey(testItem.id));
            allowedOperations.Add(Documents.OperationType.Read);
            expectedOperationCodeMap.Add(Documents.OperationType.Read, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 4, expectedOperationCodeMap);

            // Upsert an Item
            await this.container.UpsertItemAsync<dynamic>(testItem);

            allowedOperations.Add(Documents.OperationType.Upsert);
            expectedOperationCodeMap.Add(Documents.OperationType.Upsert, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 6, expectedOperationCodeMap);

            // Replace an Item
            await this.container.ReplaceItemAsync<dynamic>(testItemCreated, testItemCreated["id"].ToString());
            allowedOperations.Add(Documents.OperationType.Replace);
            expectedOperationCodeMap.Add(Documents.OperationType.Replace, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 8, expectedOperationCodeMap);

            // Patch an Item
            List<PatchOperation> patch = new List<PatchOperation>()
            {
                PatchOperation.Add("/new", "patched")
            };
            await ((ContainerInternal)this.container).PatchItemAsync<dynamic>(
                testItem.id,
                new Cosmos.PartitionKey(testItem.id),
                patch);

            allowedOperations.Add(Documents.OperationType.Patch);
            expectedOperationCodeMap.Add(Documents.OperationType.Patch, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 10, expectedOperationCodeMap);

            // Delete an Item
            await this.container.DeleteItemAsync<dynamic>(testItem.id, new Cosmos.PartitionKey(testItem.id));
            allowedOperations.Add(Documents.OperationType.Delete);
            expectedOperationCodeMap.Add(Documents.OperationType.Delete, HttpStatusCode.NoContent);
            this.AssertClientTelemetryInfo(allowedOperations, 12, expectedOperationCodeMap);
        }

        [TestMethod]
        public async Task PointFailureOperationsTest()
        {
            // Create an item with invalid Id
            var testItem = new { 
                id = "Invalid#/\\?Id",
                partitionKeyPath = "MyTestPkValue", 
                details = "it's working", 
                status = "done" 
            };

            await this.container.CreateItemAsync<dynamic>(testItem);
            List<Documents.OperationType> allowedOperations
               = new List<Documents.OperationType>(new Documents.OperationType[] {
                                Documents.OperationType.Create
               });
            IDictionary<OperationType, HttpStatusCode> expectedOperationCodeMap
              = new Dictionary<OperationType, HttpStatusCode>
              {
                    { OperationType.Create, HttpStatusCode.Created }
              };
            this.AssertClientTelemetryInfo(allowedOperations, 2, expectedOperationCodeMap);

            // Fail Read
            try
            {
                await this.container.ReadItemAsync<JObject>(
                    testItem.id, 
                    new Cosmos.PartitionKey(testItem.partitionKeyPath));
            }
            catch (CosmosException ce) when (ce.StatusCode == HttpStatusCode.NotFound)
            {
                string message = ce.ToString();
                Assert.IsNotNull(message);
            }
            allowedOperations.Add(OperationType.Read);
            expectedOperationCodeMap.Add(OperationType.Read, HttpStatusCode.NotFound);
            this.AssertClientTelemetryInfo(allowedOperations, 4, expectedOperationCodeMap);
        }

        [TestMethod]
        public async Task StreamOperationsTest()
        {
            // Create an item
            var testItem = new { id = "MyTestItemId", partitionKeyPath = "MyTestPkValue", details = "it's working", status = "done" };
            await this.container
                .CreateItemStreamAsync(TestCommon.SerializerCore.ToStream(testItem), 
                new Cosmos.PartitionKey(testItem.id));

            List<Documents.OperationType> allowedOperations
                = new List<Documents.OperationType>(new Documents.OperationType[] {
                                            Documents.OperationType.Create
                });
            IDictionary<OperationType, HttpStatusCode> expectedOperationCodeMap
             = new Dictionary<OperationType, HttpStatusCode>
             {
                    { OperationType.Create, HttpStatusCode.Created }
             };
            this.AssertClientTelemetryInfo(allowedOperations, 2, expectedOperationCodeMap);

            //Upsert an Item
            await this.container.UpsertItemStreamAsync(TestCommon.SerializerCore.ToStream(testItem), new Cosmos.PartitionKey(testItem.id));
            allowedOperations.Add(Documents.OperationType.Upsert);
            expectedOperationCodeMap.Add(Documents.OperationType.Upsert, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 4, expectedOperationCodeMap);

            //Read an Item
            await this.container.ReadItemStreamAsync(testItem.id, new Cosmos.PartitionKey(testItem.id));
            allowedOperations.Add(Documents.OperationType.Read);
            expectedOperationCodeMap.Add(Documents.OperationType.Read, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 6, expectedOperationCodeMap);

            //Replace an Item
            await this.container.ReplaceItemStreamAsync(TestCommon.SerializerCore.ToStream(testItem), testItem.id, new Cosmos.PartitionKey(testItem.id));
            allowedOperations.Add(Documents.OperationType.Replace);
            expectedOperationCodeMap.Add(Documents.OperationType.Replace, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 8, expectedOperationCodeMap);

            // Patch an Item
            List<PatchOperation> patch = new List<PatchOperation>()
            {
                PatchOperation.Add("/new", "patched")
            };
            await ((ContainerInternal)this.container).PatchItemStreamAsync(
                partitionKey: new Cosmos.PartitionKey(testItem.id),
                id: testItem.id,
                patchOperations: patch);
            allowedOperations.Add(Documents.OperationType.Patch);
            expectedOperationCodeMap.Add(Documents.OperationType.Patch, HttpStatusCode.OK);
            this.AssertClientTelemetryInfo(allowedOperations, 10, expectedOperationCodeMap);

            //Delete an Item
            await this.container.DeleteItemStreamAsync(testItem.id, new Cosmos.PartitionKey(testItem.id));
            allowedOperations.Add(Documents.OperationType.Delete);
            expectedOperationCodeMap.Add(Documents.OperationType.Delete, HttpStatusCode.NoContent);
            this.AssertClientTelemetryInfo(allowedOperations, 12, expectedOperationCodeMap);
        }

        [TestMethod]
        public async Task BatchOperationsTest()
        {
            using (BatchAsyncContainerExecutor executor = 
                new BatchAsyncContainerExecutor(
                    (ContainerInlineCore)this.container, 
                    ((ContainerInlineCore)this.container).ClientContext,
                    20,
                    Documents.Constants.MaxDirectModeBatchRequestBodySizeInBytes)
                )
            {
                List<Task<TransactionalBatchOperationResult>> tasks = new List<Task<TransactionalBatchOperationResult>>();
                for (int i = 0; i < 10; i++)
                {
                    tasks.Add(executor.AddAsync(CreateItem(i.ToString()), null, default));
                }

                await Task.WhenAll(tasks);

                List<Documents.OperationType> allowedOperations
                   = new List<Documents.OperationType>(new Documents.OperationType[] {
                                            Documents.OperationType.Batch
                   });
                IDictionary<OperationType, HttpStatusCode> expectedOperationCodeMap
                = new Dictionary<OperationType, HttpStatusCode>
                {
                    { OperationType.Batch, HttpStatusCode.OK }
                };
                this.AssertClientTelemetryInfo(allowedOperations, 2, expectedOperationCodeMap);
            }
        }

        [TestMethod]
        public async Task QueryOperationTest()
        {
            var testItem = new { id = "MyTestItemId", partitionKeyPath = "MyTestPkValue", details = "it's working", status = "done" };
            ItemResponse<object> createResponse = await this.container.CreateItemAsync<dynamic>(testItem);
            this.cosmosClient.Telemetry.Reset();
            if (createResponse.StatusCode == HttpStatusCode.Created)
            { 

                string sqlQueryText = "SELECT * FROM c";

                QueryDefinition queryDefinition = new QueryDefinition(sqlQueryText);
                FeedIterator<object> queryResultSetIterator = this.container.GetItemQueryIterator<object>(queryDefinition);

                List<object> families = new List<object>();
                while (queryResultSetIterator.HasMoreResults)
                {
                    FeedResponse<object> currentResultSet = await queryResultSetIterator.ReadNextAsync();
                    foreach (object family in currentResultSet)
                    {
                        families.Add(family);
                    }
                }
            }

            List<Documents.OperationType> allowedOperations
               = new List<Documents.OperationType>(new Documents.OperationType[] {
                                            Documents.OperationType.Query
               });
            IDictionary<OperationType, HttpStatusCode> expectedOperationCodeMap
               = new Dictionary<OperationType, HttpStatusCode>
               {
                   { OperationType.Query, HttpStatusCode.OK }
               };
            this.AssertClientTelemetryInfo(allowedOperations, 2, expectedOperationCodeMap);
        }

        private void AssertClientTelemetryInfo(
            List<Documents.OperationType> allowedOperations, 
            int operationInfoMapCount,
            IDictionary<OperationType, HttpStatusCode>  expectedOperationCodeMap)
        {
            Thread.Sleep(6000);

            Assert.AreEqual(operationInfoMapCount, this.telemetryInfo.OperationInfoMap.Count);
            foreach (KeyValuePair<ReportPayload, LongConcurrentHistogram> entry in this.telemetryInfo.OperationInfoMap)
            {
                expectedOperationCodeMap.TryGetValue(entry.Key.Operation, out HttpStatusCode expectedStatusCode);

                Assert.IsTrue(allowedOperations.Contains(entry.Key.Operation));

                Assert.AreEqual(Documents.ResourceType.Document, entry.Key.Resource);
                Assert.AreEqual((int)expectedStatusCode, entry.Key.StatusCode);

                Assert.IsTrue(this.allowedMetrics.Contains(entry.Key.MetricInfo.MetricsName));
                Assert.IsTrue(this.allowedUnitnames.Contains(entry.Key.MetricInfo.UnitName));
                Assert.IsTrue(entry.Key.MetricInfo.Max > 0);
                Assert.IsTrue(entry.Key.MetricInfo.Mean > 0);
                Assert.AreEqual(5, entry.Key.MetricInfo.Percentiles.Count, "Percentile count Not matched");
            }
        }

        private static ItemBatchOperation CreateItem(string itemId)
        {
            var testItem = new { id = itemId, Status = itemId };
            return new ItemBatchOperation(Documents.OperationType.Create, 0, new Cosmos.PartitionKey(itemId), itemId, TestCommon.SerializerCore.ToStream(testItem));
        }


    }
}
