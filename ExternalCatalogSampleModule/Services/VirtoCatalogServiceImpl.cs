﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using CacheManager.Core;
using External.CatalogModule.Web.CatalogModuleApi;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Omu.ValueInjecter;
using Top.Api;
using Top.Api.Request;
using Top.Api.Response;
using VirtoCommerce.CatalogModule.Data.Converters;
using VirtoCommerce.CatalogModule.Data.Repositories;
using VirtoCommerce.CatalogModule.Data.Services;
using VirtoCommerce.Domain.Catalog.Model;
using VirtoCommerce.Domain.Catalog.Services;
using VirtoCommerce.Domain.Commerce.Model;
using VirtoCommerce.Domain.Customer.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DynamicProperties;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SearchApiModule.Data.Model;
using VirtoCommerce.SearchModule.Core.Model.Search;
using VirtoCommerce.SearchModule.Core.Model.Search.Criterias;
using VirtoCommerce.SearchModule.Data.Providers.Lucene;
using dataModel = VirtoCommerce.CatalogModule.Data.Model;

namespace External.CatalogModule.Web.Services
{
    public class VirtoCatalogSearchImpl : CatalogServiceBase, ICatalogSearchService, IItemService, ICategoryService, ISearchQueryBuilder
    {
       private Catalog _demoVirtoCatalog = new Catalog
       {
           Id = "4974648a41df4e6ea67ef2ad76d7bbd4",
           Name = "demo.virtocommerce.com",
           Languages = new CatalogLanguage[] { new CatalogLanguage() { IsDefault = true, LanguageCode = "en-US" } }.ToList()
       };
        private readonly ICatalogSearchService _catalogSearchService;
        private readonly ICatalogModuleApiClient _vcCatalogClient;
        private readonly IItemService _productService;
        private readonly ICategoryService _categoryService;
        private readonly ISearchQueryBuilder _luceneQueryBuilder;
        private readonly IUserNameResolver _userNameResolver;
        private readonly IMemberService _memberService;
        private readonly IMemberSearchService _memberSearchService;
        private readonly ISecurityService _securityService;

        public VirtoCatalogSearchImpl(Func<ICatalogRepository> catalogRepositoryFactory, ICacheManager<object> cacheManager,
                                      ICatalogSearchService catalogSearchService, ICatalogModuleApiClient vcCatalogClient, 
                                      IItemService productService, ICategoryService categoryService, IUserNameResolver userNameResolver,
                                      ISearchQueryBuilder luceneQueryBuilder, IMemberService memberService, 
                                      ISecurityService securityService)
            :base(catalogRepositoryFactory, cacheManager)
        {
            _catalogSearchService = catalogSearchService;
            _vcCatalogClient = vcCatalogClient;
            _productService = productService;
            _categoryService = categoryService;
            _luceneQueryBuilder = luceneQueryBuilder;
            _userNameResolver = userNameResolver;
            _memberService = memberService;
            _securityService = securityService;
        }

        #region ISearchQueryBuilder
        public string DocumentType
        {
            get
            {
                return _luceneQueryBuilder.DocumentType;
            }
        }
        public object BuildQuery<T>(string scope, ISearchCriteria criteria) where T : class
        {
            var queryBuilder = _luceneQueryBuilder.BuildQuery<T>(scope, criteria) as QueryBuilder;

            if (criteria is CatalogItemSearchCriteria)
            {
                var userName = _userNameResolver.GetCurrentUserName();
                var userAccount = _securityService.FindByNameAsync(userName, UserDetails.Reduced).Result;
                if (userAccount != null && !userAccount.MemberId.IsNullOrEmpty())
                {
                    var contact = _memberService.GetByIds(new[] { userAccount.MemberId }).FirstOrDefault();
                    if (contact != null)
                    {
                        var userExperienceDp = contact.DynamicProperties.FirstOrDefault(x => x.Name.EqualsInvariant("userexperience"));
                        if (userExperienceDp != null && !userExperienceDp.Values.IsNullOrEmpty())
                        {
                            var userExpValue = userExperienceDp.Values.First().Value as DynamicPropertyDictionaryItem;
                            if (userExpValue != null)
                            {
                                var catalogSearchCriteria = criteria as CatalogItemSearchCriteria;
                                var query = queryBuilder.Query as BooleanQuery;
                                query.Add(new TermQuery(new Term(userExperienceDp.Name, userExpValue.Name)), Occur.MUST);
                            }
                        }
                    }
                }
            }
            return queryBuilder;
        }
        #endregion

        public SearchResult Search(SearchCriteria criteria)
        {
            criteria.WithHidden = false;
            var retVal = _catalogSearchService.Search(criteria);
         
            if (criteria.CatalogId.EqualsInvariant(_demoVirtoCatalog.Id) || criteria.CatalogIds.IsNullOrEmpty())
            {
                var vcSearchCriteria = new External.CatalogModule.Web.CatalogModuleApi.Models.SearchCriteria();
                vcSearchCriteria.InjectFrom(criteria);
                vcSearchCriteria.Skip = criteria.Skip;
                vcSearchCriteria.Take = criteria.Take;
                vcSearchCriteria.Sort = criteria.Sort;
                vcSearchCriteria.WithHidden = false;
                vcSearchCriteria.SearchInChildren = vcSearchCriteria.CatalogId.IsNullOrEmpty() ? true : criteria.SearchInChildren;
                vcSearchCriteria.ResponseGroup = criteria.ResponseGroup.ToString();
                vcSearchCriteria.CatalogId = "4974648a41df4e6ea67ef2ad76d7bbd4";
                var result = _vcCatalogClient.CatalogModuleSearch.Search(vcSearchCriteria);
                retVal.ProductsTotalCount += result.ProductsTotalCount.Value;
                foreach(var dtoCategory in result.Categories)
                {
                    var cat = new Category();
                    cat.InjectFrom(dtoCategory);
                    retVal.Categories.Add(cat);
                }
                foreach(var dtoProduct in result.Products)
                {
                    var prod = ConvertToProduct(dtoProduct);
                     retVal.Products.Add(prod);
                }                
            }           

            return retVal;
        }

        #region IItemService
        public CatalogProduct GetById(string itemId, ItemResponseGroup respGroup, string catalogId = null)
        {
            return GetByIds(new[] { itemId }, respGroup, catalogId).FirstOrDefault();
        }

        public CatalogProduct[] GetByIds(string[] itemIds, ItemResponseGroup respGroup, string catalogId = null)
        {
            var retVal = _productService.GetByIds(itemIds, respGroup, catalogId).ToList();
            var externalProducts = _vcCatalogClient.CatalogModuleProducts.GetProductByIds(itemIds, respGroup.ToString());
            foreach (var externalProduct in externalProducts)
            {
                var product = ConvertToProduct(externalProduct);
                var existProduct = retVal.FirstOrDefault(x => x.Id.EqualsInvariant(externalProduct.Id));
                if (existProduct != null)
                {
                    retVal.Remove(existProduct);
                    product.Properties = existProduct.Properties;
                    product.PropertyValues = existProduct.PropertyValues;
                }
                retVal.Add(product);
            }
            return retVal.ToArray();
        }

        public CatalogProduct Create(CatalogProduct item)
        {
            return _productService.Create(item);
        }

        void IItemService.Delete(string[] itemIds)
        {
            _productService.Delete(itemIds);
        }

        public void Create(CatalogProduct[] items)
        {
            _productService.Create(items);
        }

        public void Update(CatalogProduct[] items)
        {
            var externalProducts = items.Where(x => x.CatalogId.EqualsInvariant(_demoVirtoCatalog.Id));
            foreach(var externalProduct in externalProducts)
            {
                externalProduct.CategoryId = null;
                externalProduct.IsActive = false;
            }
            var topItemsIds = items.Where(x => x.CatalogId.EqualsInvariant(_demoVirtoCatalog.Id)).Select(x => x.Id).ToArray();
            var notExistProducts = topItemsIds.Except(_productService.GetByIds(topItemsIds, ItemResponseGroup.ItemInfo).Select(x => x.Id));
            if(notExistProducts.Any())
            {
                _productService.Create(items.Where(x => notExistProducts.Contains(x.Id)).ToArray());
            }
            _productService.Update(items);
        }
        #endregion

        #region Category service
        public Category[] GetByIds(string[] categoryIds, CategoryResponseGroup responseGroup, string catalogId = null)
        {
            var retVal = _categoryService.GetByIds(categoryIds, responseGroup, catalogId).ToList();
            var categoriesDto = _vcCatalogClient.CatalogModuleCategories.GetCategoriesByIds(categoryIds.ToList(), responseGroup.ToString());
            foreach(var categoryDto in categoriesDto)
            {
                var localCategory = retVal.FirstOrDefault(x => x.Id.EqualsInvariant(categoryDto.Id));
                if(localCategory == null)
                {
                    retVal.Add(ConvertToCategory(categoryDto));
                }
            }
            return retVal.ToArray();
        }

        public Category GetById(string categoryId, CategoryResponseGroup responseGroup, string catalogId = null)
        {
            return GetByIds(new string[] { categoryId }, responseGroup, catalogId).FirstOrDefault();
        }

        public void Create(Category[] categories)
        {           
            _categoryService.Create(categories);
        }

        public Category Create(Category category)
        {
           return _categoryService.Create(category);
        }

        public void Update(Category[] categories)
        {
            _categoryService.Update(categories);
        }

        public void Delete(string[] categoryIds)
        {
            _categoryService.Delete(categoryIds);
        }
        #endregion

        private Category ConvertToCategory(External.CatalogModule.Web.CatalogModuleApi.Models.Category dto)
        {
            var retVal = new Category();
            retVal.Parents = new Category[] { };
            retVal.Links = new List<CategoryLink>();
            retVal.PropertyValues = new List<PropertyValue>();
            retVal.InjectFrom(dto);

            if (!dto.Outlines.IsNullOrEmpty())
            {
                retVal.Outlines = dto.Outlines.Select(x => ConvertToOutline(x)).ToList();

            }
            return retVal;
        }
        private CatalogProduct ConvertToProduct(External.CatalogModule.Web.CatalogModuleApi.Models.Product dto)
        {
            var retVal = new dataModel.Item() { CatalogId = _demoVirtoCatalog.Id }.ToCoreModel(base.AllCachedCatalogs, base.AllCachedCategories);
            retVal.InjectFrom(dto);
            if(!dto.Images.IsNullOrEmpty())
            {
                retVal.Images = dto.Images.Select(x => new Image().InjectFrom(x) as Image).ToList();
            }
            if(!dto.Reviews.IsNullOrEmpty())
            {
                retVal.Reviews = dto.Reviews.Select(x => new EditorialReview().InjectFrom(x) as EditorialReview).ToList();
            }
            if(!dto.Outlines.IsNullOrEmpty())
            {
                retVal.Outlines = dto.Outlines.Select(x => ConvertToOutline(x)).ToList();
            
            }
            return retVal;
        }

        private Outline ConvertToOutline(External.CatalogModule.Web.CatalogModuleApi.Models.Outline outlineDto)
        {
            var outline = new Outline
            {
                Items = new List<OutlineItem>()
            };
            foreach (var itemDto in outlineDto.Items)
            {
                var item = new OutlineItem();
                item.InjectFrom(itemDto);
                item.SeoInfos = itemDto.SeoInfos.Select(x => new SeoInfo().InjectFrom(x) as SeoInfo).ToList();
                outline.Items.Add(item);
            }
            return outline;
        }

    }
}