using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using GraphQL.Resolvers;
using GraphQL.Types;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OrchardCore.Apis.GraphQL;
using OrchardCore.Apis.GraphQL.Queries;
using OrchardCore.ContentManagement.GraphQL.Queries.Types;
using OrchardCore.ContentManagement.Records;
using YesSql;
using YesSql.Services;

namespace OrchardCore.ContentManagement.GraphQL.Queries
{
    /// <summary>
    /// This type is used by <see cref="ContentTypeQuery"/> to represent a query on a content type
    /// </summary>
    public class ContentItemsFieldType : FieldType
    {
        private static ParameterExpression ContentItemParameter = Expression.Parameter(typeof(ContentItemIndex), "x");

        private static Dictionary<string, Expression> ContentItemProperties;
        private static MethodInfo IsIn = typeof(DefaultQueryExtensions).GetMethod("IsIn");
        private static MethodInfo IsNotIn = typeof(DefaultQueryExtensions).GetMethod("IsNotIn");
        private static MethodInfo Contains = typeof(string).GetMethod("Contains", new[] { typeof(string) });
        private static MethodInfo StartsWith = typeof(string).GetMethod("StartsWith", new[] { typeof(string) });
        private static MethodInfo EndsWith = typeof(string).GetMethod("EndsWith", new[] { typeof(string) });

        static ContentItemsFieldType()
        {
            ContentItemProperties = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in typeof(ContentItemIndex).GetProperties())
            {
                ContentItemProperties.Add(property.Name, ConvertToNonNullableExpression(Expression.Property(ContentItemParameter, property)));
            }
        }

        public ContentItemsFieldType(string contentItemName, ISchema schema)
        {
            Name = "ContentItems";

            Type = typeof(ListGraphType<ContentItemType>);

            var whereInput = new ContentItemWhereInput(contentItemName);
            schema.RegisterType(whereInput);

            var orderByInput = new ContentItemOrderByInput(contentItemName);
            schema.RegisterType(orderByInput);

            Arguments = new QueryArguments(
                new QueryArgument<ContentItemWhereInput> { Name = "where", Description = "filters the content items" },
                new QueryArgument<ContentItemOrderByInput> { Name = "orderBy", Description = "sort order" },
                new QueryArgument<IntGraphType> { Name = "first", Description = "the first n content items" },
                new QueryArgument<IntGraphType> { Name = "skip", Description = "the number of elements to skip" }
                new QueryArgument<PublicationStatusGraphType> { Name = "status", Description = "publication status of the content item", DefaultValue = PublicationStatusEnum.Published }
            );

            Resolver = new AsyncFieldResolver<IEnumerable<ContentItem>>(Resolve);
        }

        private async Task<IEnumerable<ContentItem>> Resolve(ResolveFieldContext context)
        {
            var graphContext = (GraphQLContext)context.UserContext;

            var versionOption = VersionOptions.Published;

            if (context.HasPopulatedArgument("status"))
            {
                versionOption = GetVersionOption(context.GetArgument<PublicationStatusEnum>("status"));
            }

            var where = context.GetArgument<Dictionary<string, object>>("where");

            var session = graphContext.ServiceProvider.GetService<ISession>();

            var query = session.Query<ContentItem, ContentItemIndex>();

            query = Filter(query, context, versionOption, where);
            query = OrderBy(query, context);

            IQuery<ContentItem> contentItemsQuery = query;
            var queryFilters = graphContext.ServiceProvider.GetServices<IGraphQLFilter<ContentItem>>().ToList();

            foreach (var filter in queryFilters)
            {
                contentItemsQuery = filter.PreQuery(query, context);
            }

            contentItemsQuery = PageQuery(contentItemsQuery, context);

            var contentItems = await contentItemsQuery.ListAsync();

            foreach (var filter in queryFilters)
            {
                contentItems = filter.PostQuery(contentItems, context);
            }

            return contentItems.ToList();
        }

        private IQuery<ContentItem> PageQuery(IQuery<ContentItem> contentItemsQuery, ResolveFieldContext context)
        {
            if (context.HasPopulatedArgument("first"))
            {
                var first = context.GetArgument<int>("first");

                contentItemsQuery = contentItemsQuery.Take(first);
            }

            if (context.HasPopulatedArgument("skip"))
            {
                var skip = context.GetArgument<int>("skip");

                contentItemsQuery = contentItemsQuery.Skip(skip);
            }

            return contentItemsQuery;
        }

        private VersionOptions GetVersionOption(PublicationStatusEnum status)
        {
            switch (status)
            {
                case PublicationStatusEnum.Published: return VersionOptions.Published;
                case PublicationStatusEnum.Draft: return VersionOptions.Draft;
                case PublicationStatusEnum.Latest: return VersionOptions.Latest;
                case PublicationStatusEnum.All: return VersionOptions.AllVersions;
                default: return VersionOptions.Published;
            }
        }

        private IQuery<ContentItem, ContentItemIndex> Filter(
            IQuery<ContentItem, ContentItemIndex> query,
            ResolveFieldContext context,
            VersionOptions versionOption,
            Dictionary<string, object> where)
        {
            // Applying version

            if (versionOption.IsPublished)
            {
                query = query.Where(q => q.Published == true);
            }
            else if (versionOption.IsDraft)
            {
                query = query.Where(q => q.Latest == true && q.Published == false);
            }
            else if (versionOption.IsLatest)
            {
                query = query.Where(q => q.Latest == true);
            }

            // Applying content type

            var contentType = ((ListGraphType)context.ReturnType).ResolvedType.Name;

            query = query.Where(q => q.ContentType == contentType);

            if (where == null)
            {
                return query;
            }

            var expressions = CreateExpression(where);

            Expression predicate = Expression.Constant(true);

            foreach (var expression in expressions)
            {
                predicate = Expression.And(predicate, expression);
            }

            query = query.Where(Expression.Lambda<Func<ContentItemIndex, bool>>(predicate, new ParameterExpression[] { ContentItemParameter }));

            return query;
        }

        private IEnumerable<Expression> CreateExpression(Dictionary<string, object> where)
        {
            foreach (var entry in where)
            {
                var values = entry.Key.Split(new[] { '_' }, 2);

                Expression comparison = null;

                Expression left, right;

                if (values[0] == "status")
                {
                    continue;
                }

                if (values.Length == 1)
                {
                    if (String.Equals(values[0], "or", StringComparison.OrdinalIgnoreCase))
                    {
                        comparison = Expression.Constant(false);

                        var subwhere = entry.Value as Dictionary<string, object>;

                        var subExpressions = CreateExpression(subwhere);

                        foreach (var subExpression in subExpressions)
                        {
                            comparison = Expression.Or(comparison, subExpression);
                        }
                    }
                    else if (String.Equals(values[0], "and", StringComparison.OrdinalIgnoreCase))
                    {
                        comparison = Expression.Constant(true);

                        var subwhere = entry.Value as Dictionary<string, object>;

                        var subExpressions = CreateExpression(subwhere);

                        foreach (var subExpression in subExpressions)
                        {
                            comparison = Expression.And(comparison, subExpression);
                        }
                    }
                    else
                    {
                        left = ContentItemProperties[values[0]];
                        right = Expression.Constant(entry.Value);

                        comparison = Expression.Equal(left, right);
                    }
                }
                else
                {
                    left = ContentItemProperties[values[0]];
                    right = Expression.Constant(entry.Value);

                    switch (values[1])
                    {
                        case "not": comparison = Expression.NotEqual(left, right); break;
                        case "gt": comparison = Expression.GreaterThan(left, right); break;
                        case "gte": comparison = Expression.GreaterThanOrEqual(left, right); break;
                        case "lt": comparison = Expression.LessThan(left, right); break;
                        case "lte": comparison = Expression.LessThanOrEqual(left, right); break;
                        case "contains": comparison = Expression.Call(left, Contains, right); ; break;
                        case "not_contains": comparison = Expression.Not(Expression.Call(left, Contains, right)); break;
                        case "starts_with": comparison = Expression.Call(left, StartsWith, right); ; break;
                        case "not_starts_with": comparison = Expression.Not(Expression.Call(left, StartsWith, right)); break;
                        case "ends_with": comparison = Expression.Call(left, EndsWith, right); ; break;
                        case "not_ends_with": comparison = Expression.Not(Expression.Call(left, EndsWith, right)); break;
                        case "in": comparison = Expression.Call(null, IsIn, left, right); break;
                        case "not_in": comparison = Expression.Call(null, IsNotIn, left, right); break;

                        default: comparison = Expression.Equal(left, right); break;
                    }
                }

                yield return comparison;
            }
        }

        private IQuery<ContentItem, ContentItemIndex> OrderBy(IQuery<ContentItem, ContentItemIndex> query,
            ResolveFieldContext context)
        {
            if (context.HasPopulatedArgument("orderBy"))
            {
                var orderByArguments = JObject.FromObject(context.Arguments["orderBy"]);

                if (orderByArguments != null)
                {
                    var thenBy = false;

                    foreach (var property in orderByArguments.Properties())
                    {
                        var direction = (OrderByDirection)property.Value.Value<int>();

                        Expression<Func<ContentItemIndex, object>> selector = null;

                        switch (property.Name)
                        {
                            case "contentItemId": selector = x => x.ContentItemId; break;
                            case "contentItemVersionId": selector = x => x.ContentItemVersionId; break;
                            case "displayText": selector = x => x.DisplayText; break;
                            case "published": selector = x => x.Published; break;
                            case "latest": selector = x => x.Latest; break;
                            case "createdUtc": selector = x => x.CreatedUtc; break;
                            case "modifiedUtc": selector = x => x.ModifiedUtc; break;
                            case "publishedUtc": selector = x => x.PublishedUtc; break;
                            case "owner": selector = x => x.Owner; break;
                            case "author": selector = x => x.Author; break;
                        }

                        if (selector != null)
                        {
                            if (!thenBy)
                            {
                                query = direction == OrderByDirection.Ascending
                                        ? query.OrderBy(selector)
                                        : query.OrderByDescending(selector)
                                    ;
                            }
                            else
                            {
                                query = direction == OrderByDirection.Ascending
                                        ? query.ThenBy(selector)
                                        : query.ThenByDescending(selector)
                                    ;
                            }

                            thenBy = true;
                        }
                    }
                }
            }
            else
            {
                query = query.OrderByDescending(x => x.CreatedUtc);
            }

            return query;
        }

        static Expression ConvertToNonNullableExpression(Expression expression)
        {
            if (expression == null)
            {
                throw new ArgumentNullException("expression");
            }

            if (expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return Expression.Convert(expression, expression.Type.GetGenericArguments()[0]);
            }
            else
            {
                return expression;
            }
        }

    }
}