using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Orders;
using Nop.Services.Catalog;
using Nop.Services.Payments;
using Nop.Services.Customers;
using System;
using System.Collections.Generic;
using System.Linq;
using Uol.PagSeguro;

namespace NopBrasil.Plugin.Payments.PagSeguro.Services
{
    public class PagSeguroService : IPagSeguroService
    {
        //todo: colocar a moeda utilizada como configuração
        private const string CURRENCY_CODE = "BRL";

        private readonly ICustomerService _customerService;
        private readonly ICountryService _countryService;
        private readonly IStateProvinceService _stateProvinceService;
        private readonly ICurrencyService _currencyService;
        private readonly IProductService _productService;
        private readonly CurrencySettings _currencySettings;
        private readonly PagSeguroPaymentSetting _pagSeguroPaymentSetting;
        private readonly IOrderService _orderService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IStoreContext _storeContext;

        public PagSeguroService(ICustomerService customerService,
                                ICountryService countryService,
                                IStateProvinceService stateProvinceService,
                                IProductService productService,
                                ISettingService settingService, 
                                ICurrencyService currencyService, 
                                CurrencySettings currencySettings, 
                                PagSeguroPaymentSetting pagSeguroPaymentSetting, 
                                IOrderService orderService, 
                                IOrderProcessingService orderProcessingService, 
                                IStoreContext storeContext)
        {
            _customerService = customerService;
            _countryService = countryService;
            _stateProvinceService = stateProvinceService;
            _productService = productService;
            _currencyService = currencyService;
            _currencySettings = currencySettings;
            _pagSeguroPaymentSetting = pagSeguroPaymentSetting;
            _orderService = orderService;
            _orderProcessingService = orderProcessingService;
            _storeContext = storeContext;
        }

        public Uri CreatePayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            // Seta as credenciais
            AccountCredentials credentials = new AccountCredentials(@_pagSeguroPaymentSetting.PagSeguroEmail, @_pagSeguroPaymentSetting.PagSeguroToken);

            PaymentRequest payment = new PaymentRequest();
            payment.Currency = CURRENCY_CODE;
            payment.Reference = postProcessPaymentRequest.Order.Id.ToString();

            LoadingItems(postProcessPaymentRequest, payment);
            LoadingShipping(postProcessPaymentRequest, payment);
            LoadingSender(postProcessPaymentRequest, payment);

            return Uol.PagSeguro.PaymentService.Register(credentials, payment);
        }

        private void LoadingSender(PostProcessPaymentRequest postProcessPaymentRequest, PaymentRequest payment)
        {
            var customer = _customerService.GetCustomerById(postProcessPaymentRequest.Order.CustomerId);
            var billingAddress = _customerService.GetCustomerBillingAddress(customer);
            payment.Sender = new Sender();
            payment.Sender.Email = customer.Email;
            payment.Sender.Name = $"{billingAddress.FirstName} {billingAddress.LastName}";
        }

        private decimal GetConvertedRate(decimal rate)
        {
            var usedCurrency = _currencyService.GetCurrencyByCode(CURRENCY_CODE);
            if (usedCurrency == null)
                throw new NopException($"PagSeguro payment service. Could not load \"{CURRENCY_CODE}\" currency");

            if (usedCurrency.CurrencyCode == _currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId).CurrencyCode)
                return rate;
            else
                return _currencyService.ConvertFromPrimaryStoreCurrency(rate, usedCurrency);
        }

        private void LoadingShipping(PostProcessPaymentRequest postProcessPaymentRequest, PaymentRequest payment)
        {
            var customer = _customerService.GetCustomerById(postProcessPaymentRequest.Order.CustomerId);
            var shippingAddress = _customerService.GetCustomerShippingAddress(customer);

            payment.Shipping = new Shipping();
            payment.Shipping.ShippingType = ShippingType.NotSpecified;
            Address adress = new Address();
            adress.Complement = string.Empty;
            adress.District = string.Empty;
            adress.Number = string.Empty;
            if (shippingAddress != null)
            {
                var country = _countryService.GetCountryByAddress(shippingAddress);
                var stateProvince = _stateProvinceService.GetStateProvinceByAddress(shippingAddress);

                adress.City = shippingAddress.City;
                adress.Country = country.Name;
                adress.PostalCode = shippingAddress.ZipPostalCode;
                adress.State = stateProvince.Name;
                adress.Street = shippingAddress.Address1;
            }
            payment.Shipping.Cost = Math.Round(GetConvertedRate(postProcessPaymentRequest.Order.OrderShippingInclTax), 2);
        }

        private void LoadingItems(PostProcessPaymentRequest postProcessPaymentRequest, PaymentRequest payment)
        {
            var orderItems = _orderService.GetOrderItems(postProcessPaymentRequest.Order.Id);

            foreach (var items in orderItems)
            {
                var product = _productService.GetProductById(items.ProductId);

                Item item = new Item();
                item.Amount = Math.Round(GetConvertedRate(items.UnitPriceInclTax), 2);
                item.Description = product.Name;
                item.Id = items.Id.ToString();
                item.Quantity = items.Quantity;
                if (items.ItemWeight.HasValue)
                    item.Weight = Convert.ToInt64(items.ItemWeight);
                payment.Items.Add(item);
            }
        }

        private IEnumerable<Order> GetPendingOrders() => _orderService.SearchOrders(_storeContext.CurrentStore.Id, paymentMethodSystemName: "Payments.PagSeguro", psIds: new List<int>() { 10 }).Where(o => _orderProcessingService.CanMarkOrderAsPaid(o));

        private TransactionSummary GetTransaction(AccountCredentials credentials, string referenceCode) => TransactionSearchService.SearchByReference(credentials, referenceCode)?.Items?.FirstOrDefault();

        private bool TransactionIsPaid(TransactionSummary transaction) => (transaction?.TransactionStatus == TransactionStatus.Paid || transaction?.TransactionStatus == TransactionStatus.Available);

        public void CheckPayments()
        {
            AccountCredentials credentials = new AccountCredentials(@_pagSeguroPaymentSetting.PagSeguroEmail, @_pagSeguroPaymentSetting.PagSeguroToken);
            foreach (var order in GetPendingOrders())
                if (TransactionIsPaid(GetTransaction(credentials, order.Id.ToString())))
                    _orderProcessingService.MarkOrderAsPaid(order);
        }
    }
}