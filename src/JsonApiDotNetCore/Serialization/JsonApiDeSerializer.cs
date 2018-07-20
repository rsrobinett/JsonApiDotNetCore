using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JsonApiDotNetCore.Extensions;
using JsonApiDotNetCore.Internal;
using JsonApiDotNetCore.Internal.Generics;
using JsonApiDotNetCore.Models;
using JsonApiDotNetCore.Models.Operations;
using JsonApiDotNetCore.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonApiDotNetCore.Serialization
{
    public class JsonApiDeSerializer : IJsonApiDeSerializer
    {
        private readonly IJsonApiContext _jsonApiContext;

        [Obsolete(
            "The deserializer no longer depends on the IGenericProcessorFactory",
            error: false)]
        public JsonApiDeSerializer(
            IJsonApiContext jsonApiContext,
            IGenericProcessorFactory genericProcessorFactory)
        {
            _jsonApiContext = jsonApiContext;
        }

        public JsonApiDeSerializer(IJsonApiContext jsonApiContext)
        {
            _jsonApiContext = jsonApiContext;
        }

        public object Deserialize(string requestBody)
        {
            try
            {
                var bodyJToken = JToken.Parse(requestBody);

                if (RequestIsOperation(bodyJToken))
                {
                    _jsonApiContext.IsBulkOperationRequest = true;

                    // TODO: determine whether or not the token should be re-used rather than performing full
                    // deserialization again from the string
                    var operations = JsonConvert.DeserializeObject<OperationsDocument>(requestBody);
                    if (operations == null)
                        throw new JsonApiException(400, "Failed to deserialize operations request.");

                    return operations;
                }

                var document = bodyJToken.ToObject<Document>();

                _jsonApiContext.DocumentMeta = document.Meta;
                var entity = DocumentToObject(document.Data, document.Included);
                return entity;
            }
            catch (JsonApiException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new JsonApiException(400, "Failed to deserialize request body", e);
            }
        }

        private bool RequestIsOperation(JToken bodyJToken)
            => _jsonApiContext.Options.EnableOperations
                && (bodyJToken.SelectToken("operations") != null);

        public TEntity Deserialize<TEntity>(string requestBody) => (TEntity)Deserialize(requestBody);

        public object DeserializeRelationship(string requestBody)
        {
            try
            {
                var data = JToken.Parse(requestBody)["data"];

                if (data is JArray)
                    return data.ToObject<List<DocumentData>>();

                return new List<DocumentData> { data.ToObject<DocumentData>() };
            }
            catch (Exception e)
            {
                throw new JsonApiException(400, "Failed to deserialize request body", e);
            }
        }

        public List<TEntity> DeserializeList<TEntity>(string requestBody)
        {
            try
            {
                var documents = JsonConvert.DeserializeObject<Documents>(requestBody);

                var deserializedList = new List<TEntity>();
                foreach (var data in documents.Data)
                {
                    var entity = (TEntity)DocumentToObject(data, documents.Included);
                    deserializedList.Add(entity);
                }

                return deserializedList;
            }
            catch (Exception e)
            {
                throw new JsonApiException(400, "Failed to deserialize request body", e);
            }
        }

        public object DocumentToObject(DocumentData data, List<DocumentData> included = null)
        {
            if (data == null)
                throw new JsonApiException(422, "Failed to deserialize document as json:api.");

            var contextEntity = _jsonApiContext.ContextGraph.GetContextEntity(data.Type?.ToString());
            _jsonApiContext.RequestEntity = contextEntity ?? throw new JsonApiException(400,
                    message: $"This API does not contain a json:api resource named '{data.Type}'.",
                    detail: "This resource is not registered on the ContextGraph. "
                            + "If you are using Entity Framework, make sure the DbSet matches the expected resource name. "
                            + "If you have manually registered the resource, check that the call to AddResource correctly sets the public name."); ;

            var entity = Activator.CreateInstance(contextEntity.EntityType);

            entity = SetEntityAttributes(entity, contextEntity, data.Attributes);
            entity = SetRelationships(entity, contextEntity, data.Relationships, included);

            var identifiableEntity = (IIdentifiable)entity;

            if (data.Id != null)
                identifiableEntity.StringId = data.Id?.ToString();

            return identifiableEntity;
        }

        private object SetEntityAttributes(
            object entity, ContextEntity contextEntity, Dictionary<string, object> attributeValues)
        {
            if (attributeValues == null || attributeValues.Count == 0)
                return entity;

            foreach (var attr in contextEntity.Attributes)
            {
                if (attributeValues.TryGetValue(attr.PublicAttributeName, out object newValue))
                {
                    var convertedValue = ConvertAttrValue(newValue, attr.PropertyInfo.PropertyType);
                    attr.SetValue(entity, convertedValue);

                    if (attr.IsImmutable == false)
                        _jsonApiContext.AttributesToUpdate[attr] = convertedValue;
                }
            }

            return entity;
        }

        private object ConvertAttrValue(object newValue, Type targetType)
        {
            if (newValue is JContainer jObject)
                return DeserializeComplexType(jObject, targetType);

            var convertedValue = TypeHelper.ConvertType(newValue, targetType);
            return convertedValue;
        }

        private object DeserializeComplexType(JContainer obj, Type targetType)
        {
            return obj.ToObject(targetType, JsonSerializer.Create(_jsonApiContext.Options.SerializerSettings));
        }

        private object SetRelationships(
            object entity,
            ContextEntity contextEntity,
            Dictionary<string, RelationshipData> relationships,
            List<DocumentData> included = null)
        {
            if (relationships == null || relationships.Count == 0)
                return entity;

            var entityProperties = entity.GetType().GetProperties();

            foreach (var attr in contextEntity.Relationships)
            {
                entity = attr.IsHasOne
                    ? SetHasOneRelationship(entity, entityProperties, (HasOneAttribute)attr, contextEntity, relationships, included)
                    : SetHasManyRelationship(entity, entityProperties, attr, contextEntity, relationships, included);
            }

            return entity;
        }

        private object SetHasOneRelationship(object entity,
            PropertyInfo[] entityProperties,
            HasOneAttribute attr,
            ContextEntity contextEntity,
            Dictionary<string, RelationshipData> relationships,
            List<DocumentData> included = null)
        {
            var relationshipName = attr.PublicRelationshipName;

            if (relationships.TryGetValue(relationshipName, out RelationshipData relationshipData) == false)
                return entity;

            var relationshipAttr = _jsonApiContext.RequestEntity.Relationships
                .SingleOrDefault(r => r.PublicRelationshipName == relationshipName);

            if (relationshipAttr == null)
                throw new JsonApiException(400, $"{_jsonApiContext.RequestEntity.EntityName} does not contain a relationship '{relationshipName}'");

            var rio = (ResourceIdentifierObject)relationshipData.ExposedData;

            var foreignKey = attr.IdentifiablePropertyName;
            var foreignKeyProperty = entityProperties.FirstOrDefault(p => p.Name == foreignKey);

            if (foreignKeyProperty == null && rio == null)
                return entity;

            var foreignKeyPropertyValue = rio?.Id ?? null;
            if (foreignKeyProperty != null)
            {
                // in the case of the HasOne independent side of the relationship, we should still create the shell entity on the other side
                // we should not actually require the resource to have a foreign key (be the dependent side of the relationship)

                // e.g. PATCH /articles
                // {... { "relationships":{ "Owner": { "data": null } } } }
                if (rio == null && Nullable.GetUnderlyingType(foreignKeyProperty.PropertyType) == null)
                    throw new JsonApiException(400, $"Cannot set required relationship identifier '{attr.IdentifiablePropertyName}' to null because it is a non-nullable type.");

                var convertedValue = TypeHelper.ConvertType(foreignKeyPropertyValue, foreignKeyProperty.PropertyType);
                foreignKeyProperty.SetValue(entity, convertedValue);
                _jsonApiContext.RelationshipsToUpdate[relationshipAttr] = convertedValue;
            }

            if (rio != null
                // if the resource identifier is null, there should be no reason to instantiate an instance
                && rio.Id != null)
            {
                // we have now set the FK property on the resource, now we need to check to see if the
                // related entity was included in the payload and update its attributes
                var includedRelationshipObject = GetIncludedRelationship(rio, included, relationshipAttr);
                if (includedRelationshipObject != null)
                    relationshipAttr.SetValue(entity, includedRelationshipObject);

                // we need to store the fact that this relationship was included in the payload
                // for EF, the repository will use these pointers to make ensure we don't try to
                // create resources if they already exist, we just need to create the relationship
                _jsonApiContext.HasOneRelationshipPointers.Add(attr, includedRelationshipObject);
            }

            return entity;
        }

        private object SetHasManyRelationship(object entity,
            PropertyInfo[] entityProperties,
            RelationshipAttribute attr,
            ContextEntity contextEntity,
            Dictionary<string, RelationshipData> relationships,
            List<DocumentData> included = null)
        {
            var relationshipName = attr.PublicRelationshipName;

            if (relationships.TryGetValue(relationshipName, out RelationshipData relationshipData))
            {
                var data = (List<ResourceIdentifierObject>)relationshipData.ExposedData;

                if (data == null) return entity;

                var relatedResources = relationshipData.ManyData.Select(r =>
                {
                    var instance = GetIncludedRelationship(r, included, attr);
                    return instance;
                });

                var convertedCollection = TypeHelper.ConvertCollection(relatedResources, attr.Type);

                attr.SetValue(entity, convertedCollection);

                _jsonApiContext.HasManyRelationshipPointers.Add(attr, convertedCollection);
            }

            return entity;
        }

        private IIdentifiable GetIncludedRelationship(ResourceIdentifierObject relatedResourceIdentifier, List<DocumentData> includedResources, RelationshipAttribute relationshipAttr)
        {
            // at this point we can be sure the relationshipAttr.Type is IIdentifiable because we were able to successfully build the ContextGraph
            var relatedInstance = relationshipAttr.Type.New<IIdentifiable>();
            relatedInstance.StringId = relatedResourceIdentifier.Id;

            // can't provide any more data other than the rio since it is not contained in the included section
            if (includedResources == null || includedResources.Count == 0)
                return relatedInstance;

            var includedResource = GetLinkedResource(relatedResourceIdentifier, includedResources);
            if (includedResource == null)
                return relatedInstance;

            var contextEntity = _jsonApiContext.ContextGraph.GetContextEntity(relationshipAttr.Type);
            if (contextEntity == null)
                throw new JsonApiException(400, $"Included type '{relationshipAttr.Type}' is not a registered json:api resource.");

            SetEntityAttributes(relatedInstance, contextEntity, includedResource.Attributes);

            return relatedInstance;
        }

        private DocumentData GetLinkedResource(ResourceIdentifierObject relatedResourceIdentifier, List<DocumentData> includedResources)
        {
            try
            {
                return includedResources.SingleOrDefault(r => r.Type == relatedResourceIdentifier.Type && r.Id == relatedResourceIdentifier.Id);
            }
            catch (InvalidOperationException e)
            {
                throw new JsonApiException(400, $"A compound document MUST NOT include more than one resource object for each type and id pair."
                        + $"The duplicate pair was '{relatedResourceIdentifier.Type}, {relatedResourceIdentifier.Id}'", e);
            }
        }
    }
}
