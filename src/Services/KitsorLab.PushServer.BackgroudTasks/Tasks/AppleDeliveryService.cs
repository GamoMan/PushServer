﻿namespace KitsorLab.PushServer.BackgroudTasks.Tasks
{
	using KitsorLab.PushServer.PNS.ApplePush.Models;
	using KitsorLab.PushServer.BackgroudTasks.Configuration;
	using KitsorLab.PushServer.BackgroudTasks.Queries;
	using KitsorLab.PushServer.BackgroudTasks.Services;
	using KitsorLab.PushServer.BackgroudTasks.Tasks.Base;
	using KitsorLab.PushServer.BackgroudTasks.Tasks.Queue;
	using KitsorLab.PushServer.Kernel.Models.Delivery;
	using KitsorLab.PushServer.Kernel.Models.Subscription;
	using KitsorLab.PushServer.Kernel.SeedWork;
	using Microsoft.Extensions.DependencyInjection;
	using Microsoft.Extensions.Logging;
	using Microsoft.Extensions.Options;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using PushSubscription = Kernel.Models.Subscription.Subscription;


	public class AppleDeliveryService : BackgroundService
	{
		private readonly IServiceProvider _services;
		private readonly IDeliveryQueries _deliveryQueries;
		private readonly AppleDeliveryTaskSettings _settings;
		private readonly IAppleDeliveryItemsQueue _queue;
		private readonly IAppleWebPushService _pushService;
		private readonly ILogger<AppleDeliveryService> _logger;

		public AppleDeliveryService(
			IServiceProvider services,
			IDeliveryQueries deliveryQueries,
			IOptions<AppleDeliveryTaskSettings> settings,
			IAppleDeliveryItemsQueue queue,
			IAppleWebPushService pushService,
			ILogger<AppleDeliveryService> logger)
		{
			_services = services ?? throw new ArgumentNullException(nameof(services));
			_deliveryQueries = deliveryQueries ?? throw new ArgumentNullException(nameof(deliveryQueries));
			_settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
			_queue = queue ?? throw new ArgumentNullException(nameof(queue));
			_pushService = pushService ?? throw new ArgumentException(nameof(pushService));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <param name="stoppingToken"></param>
		/// <returns></returns>
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			_logger.LogDebug($"[TASK] AppleDeliveryService is starting.");
			stoppingToken.Register(() => _logger.LogDebug($"[TASK] AppleDeliveryService background task is stopping."));

			while (!stoppingToken.IsCancellationRequested)
			{
				_logger.LogDebug($"[TASK] AppleDeliveryService background task is doing background work.");

				int numEntities = 0;
				try
				{
					numEntities = await DoWork();
				}
				catch (Exception ex)
				{
					_logger.LogError($"[TASK] AppleDeliveryService got Exception: {ex.Message}, Trace: {ex.StackTrace}");
				}

				if (numEntities == 0)
				{
					await Task.Delay(TimeSpan.FromSeconds(_settings.CheckUpdateTime), stoppingToken);
				}
			}

			_logger.LogDebug($"[TASK] AppleDeliveryService background task is stopping.");

			await Task.CompletedTask;
		}

		/// <returns></returns>
		private async Task<int> DoWork()
		{
			List<long> keys = GetItems();
			if (!keys.Any()) return 0;

			var tasks = keys.Select(x => DeliveryNotificationsAsync(x));
			ProcessResult<DeliveryModel>[] results = await Task.WhenAll(tasks);
			await HandleResults(results);

			return keys.Count;
		}

		/// <returns></returns>
		private List<long> GetItems()
		{
			int limit = 1;
			List<long> keys = new List<long>();
			bool hasItem = false;

			do
			{
				hasItem = _queue.DequeueItem(out long entityId);
				if (hasItem)
					keys.Add(entityId);
			}
			while (keys.Count <= limit && hasItem);

			return keys;
		}

		/// <returns></returns>
		private async Task<ProcessResult<DeliveryModel>> DeliveryNotificationsAsync(long deliveryKey)
		{
			ProcessResult<DeliveryModel> retVal;

			try
			{
				DeliveryModel dmodel = await _deliveryQueries.GetDeliveryAsync(deliveryKey);

				var aps = new Aps(dmodel.Notification.Title, dmodel.Notification.Message, string.Empty, 
				    new List<string> { dmodel.Notification.NotificationId.ToString() });
				ProcessResult result = await _pushService.SendAsync(dmodel.Subscription.DeviceToken, aps);

				retVal = new ProcessResult<DeliveryModel>(dmodel);
				retVal.PopulateErrorFromResult(result);
			}
			catch (Exception ex)
			{
				retVal = new ProcessResult<DeliveryModel>();
				retVal.SetErrorInfo($"DeliveryNotification Error: {ex.Message}");
			}

			return retVal;
		}

		/// <param name="results"></param>
		private async Task HandleResults(ProcessResult<DeliveryModel>[] results)
		{
			List<ProcessResult<DeliveryModel>> failures = results.Failures().ToList();
			List<DeliveryModel> successes = results.Successes().ToList();
			List<int> invalidSubscriptionCodes = new List<int> { 410 };

			using (IServiceScope scope = _services.CreateScope())
			{
				IServiceProvider services = scope.ServiceProvider;
				IDeliveryRepository deliveryRepository = services.GetRequiredService<IDeliveryRepository>();
				ISubscriptionRepository subscriptionRepository = services.GetRequiredService<ISubscriptionRepository>();

				List<Delivery> deliveries = await deliveryRepository.GetByKeysAsync(results.Select(x => x.ReturnValue.DeliveryKey), false);

				// handling the results
				failures.ForEach(async x =>
				{
					if (invalidSubscriptionCodes.Contains(x.ErrorCode))
					{

						await subscriptionRepository.DeleteAsync(new PushSubscription()
						{
							SubscriptionKey = x.ReturnValue.Subscription.SubscriptionKey
						});
						_logger.LogDebug($"[AppleDeliveryService] Invalid subscription {x.ReturnValue.Subscription.SubscriptionKey}. Deleted.");

					}
					else
					{

						string errMsg = $"[AppleDeliveryService] Can't send notification [DeliveryKey: {x.ReturnValue.DeliveryKey}]";
						errMsg += $" for subscription {x.ReturnValue.Subscription.SubscriptionKey}. Response code: {x.ErrorCode}, Message: {x.ErrorMsg}";
						_logger.LogError(errMsg);

						Delivery delivery = deliveries.FirstOrDefault(e => e.DeliveryKey == x.ReturnValue.DeliveryKey);
						if (delivery != null)
						{
							delivery.SetUnknownErrorStatus();
						}

					}
				});

				successes.ForEach(x =>
				{
					Delivery delivery = deliveries.FirstOrDefault(e => e.DeliveryKey == x.DeliveryKey);
					if (delivery != null)
					{
						delivery.SetHasBeenSentStatus();
					}
				});

				await subscriptionRepository.UnitOfWork.SaveEntitiesAsync();
				await deliveryRepository.UnitOfWork.SaveEntitiesAsync();
			}
		}

	}
}
