# FlowValidator - 链式验证框架       [English](https://github.com/8720826/FlowValidator/blob/main/README.en.md)

## 简介
`FlowValidator` 是一个为复杂业务场景设计的链式验证框架。它支持同步和异步混合验证、实体依赖管理、跨实体联合校验，并提供了全流程控制（成功/失败回调和终态处理）。其核心设计目标是解决业务验证中的实体依赖、异步操作和复杂规则组合问题。

## 核心特性
- **链式调用**：流畅的API设计，支持按顺序添加实体和验证规则
- **混合验证**：同时支持同步和异步验证操作
- **实体依赖管理**：后续实体可以依赖前面添加的实体进行构建
- **交叉验证**：支持多个实体之间的联合验证规则
- **全流程控制**：提供验证成功、失败和最终回调，可访问验证过程中的所有实体
- **短路机制**：当遇到第一个验证失败时，后续验证将不再执行


## 快速开始

### 安装
```bash
Install-Package FlowValidator
```

### 基础使用
```csharp
var validator = new FlowValidator();

validator.Add(() => request.Username, out var usernameRef)
         .CheckNotNullOrEmpty(usernameRef, "用户名不能为空")
         .Check(usernameRef, name => name.Length >= 5, "用户名至少5个字符");

validator.Add(() => request.Email, out var emailRef)
         .CheckNotNullOrEmpty(emailRef, "邮箱不能为空")
         .Check(emailRef, email => Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"), "邮箱格式无效");

var result = await validator.ValidateAsync();

if (!result.IsValid) 
    return BadRequest(result.ToString());
```

## 使用示例

### 示例1：用户注册验证（同步+异步混合）
```csharp
public async Task RegisterUser(UserRegistrationDto dto)
{
    var validator = new FlowValidator();
    
    // 添加基础实体（同步）
    validator.Add(() => dto.Username, out var usernameRef)
             .CheckNotNullOrEmpty(usernameRef);
    
    validator.Add(() => dto.Password, out var passwordRef)
             .CheckNotNullOrEmpty(passwordRef, "密码不能为空");
    
    // 异步验证用户名唯一性
    validator.CheckAsync(usernameRef, async name => 
        (await _userService.IsUsernameUnique(name), "用户名已被占用")
    );
    
    // 添加依赖实体验证（密码强度）
    validator.ThenAdd(entities => 
        new PasswordStrengthAnalyzer().Analyze(
            passwordRef.GetValue(validator)
        ), out var strengthRef
    ).Check(strengthRef, s => 
        (s.Score >= 8, $"密码强度不足（当前强度：{s.Score}/10）")
    );
    
    // 执行验证流程
    await validator.StartFlow()
        .OnSuccess(entities => {
            var user = BuildUser(entities);
            _userRepository.Add(user);
        })
        .OnFailure((result, entities) => {
            _logger.Warn($"注册失败: {result}");
            // 使用实体数据发送验证邮件
            var username = entities.TryGet<string>(usernameRef.Name);
            SendVerificationEmail(username);
        })
        .ExecuteAsync();
}
```

### 示例2：订单支付流程（多实体交叉验证）
```csharp
public async Task ProcessPayment(PaymentRequest request)
{
    var validator = new FlowValidator();
    
    // 添加基础实体
    validator.Add(() => request.OrderId, out var orderIdRef);
    validator.Add(() => request.PaymentMethod, out var methodRef);
    
    // 异步获取订单
    validator.AddAsync(async () => 
        await _orderService.GetOrderAsync(orderIdRef.GetValue(validator)), 
        out var orderRef
    ).CheckNotNull(orderRef, "订单不存在");
    
    // 异步获取用户
    validator.AddAsync(async entities => 
        await _userService.GetUserAsync(
            orderRef.GetValue(validator).UserId
        ), out var userRef
    ).CheckNotNull(userRef, "用户不存在");
    
    // 交叉验证：支付方式是否支持
    validator.CheckCross(
        methodRef, 
        userRef,
        (method, user) => user.SupportedPaymentMethods.Contains(method),
        "该支付方式不可用"
    );
    
    // 交叉验证：余额是否充足（异步）
    validator.CheckCrossAsync(
        orderRef, 
        userRef,
        async (order, user) => 
            (await _paymentService.GetBalanceAsync(user.Id)) >= order.TotalAmount,
        "账户余额不足"
    );
    
    // 执行流程
    await validator.StartFlow()
        .OnSuccessAsync(async entities => {
            var order = orderRef.GetValue(validator);
            await _paymentService.ChargeAsync(order.TotalAmount);
        })
        .OnFailureAsync(async (result, entities) => {
            var order = entities.TryGet<Order>(orderRef.Name);
            if (order != null)
                await _notificationService.SendPaymentFailedAsync(order.UserId);
        })
        .Finally(result => _metrics.TrackPaymentValidation(result.IsValid))
        .ExecuteAsync();
}
```

### 示例3：库存更新（复杂依赖链）
```csharp
public async Task UpdateInventory(InventoryUpdateCommand command)
{
    var validator = new FlowValidator();
    
    // 添加基础实体
    validator.Add(() => command.ProductId, out var productIdRef);
    validator.Add(() => command.WarehouseId, out var warehouseIdRef);
    
    // 异步获取产品
    validator.AddAsync(async () => 
        await _productService.GetProductAsync(productIdRef.GetValue(validator)), 
        out var productRef
    ).CheckNotNull(productRef, "产品不存在");
    
    // 依赖产品获取库存策略
    validator.ThenAdd(entities => 
        _inventoryPolicyFactory.GetPolicy(productRef.GetValue(validator).Category),
        out var policyRef
    );
    
    // 异步获取当前库存
    validator.AddAsync(async entities => {
        var product = productRef.GetValue(validator);
        var warehouse = warehouseIdRef.GetValue(validator);
        return await _inventoryService.GetStockAsync(product.Sku, warehouse);
    }, out var stockRef);
    
    // 复杂业务规则验证
    validator.CheckCross(
        command, 
        policyRef, 
        stockRef,
        (cmd, policy, stock) => policy.ValidateUpdate(stock, cmd.Adjustment),
        "库存调整违反业务规则"
    );
    
    // 执行流程
    await validator.StartFlow()
        .OnSuccess(entities => {
            var stock = stockRef.GetValue(validator);
            stock.Adjust(command.Adjustment);
            _inventoryRepository.Update(stock);
        })
        .OnFailure((result, entities) => 
            _alertService.Trigger("inventory_violation", result.ErrorMessage)
        )
        .ExecuteAsync();
}
```

### 示例4：API参数验证（极简模式）
```csharp
public async Task<IActionResult> CreatePost([FromBody] PostCreateRequest request)
{
    var validator = new FlowValidator();
    
    validator.Add(() => request.Title, out var titleRef)
             .CheckNotNullOrEmpty(titleRef, "标题不能为空")
             .Check(titleRef, t => t.Length <= 100, "标题过长");
    
    validator.Add(() => request.Content, out var contentRef)
             .CheckNotNullOrEmpty(contentRef);
    
    validator.Add(() => request.AuthorId, out var authorIdRef)
             .Check(authorIdRef, id => id > 0, "无效作者ID");
    
    var result = await validator.ValidateAsync();
    
    if (!result.IsValid) 
        return BadRequest(result.ToString());
    
    // 创建逻辑...
    return Ok();
}
```

## 核心类说明

### `FlowValidator`
核心验证器类，提供链式调用方法：
- `Add()`: 添加同步实体
- `AddAsync()`: 添加异步实体
- `ThenAdd()`: 添加依赖前面实体的同步实体
- `ThenAddAsync()`: 添加依赖前面实体的异步实体
- `Check()`: 添加同步验证
- `CheckAsync()`: 添加异步验证
- `StartFlow()`: 启动验证流程构建器

### `ValidationFlow`
验证流程控制器：
- `OnSuccess()`: 验证成功回调（可访问所有实体）
- `OnFailure()`: 验证失败回调（可访问错误信息和实体）
- `Finally()`: 最终处理（无论成功失败都会执行）
- `ExecuteAsync()`: 执行验证流程

### `EntityRef<T>`
实体引用包装器，用于安全地引用验证过程中的实体。

### `ValidationResult`
验证结果容器，包含：
- `IsValid`: 验证是否通过
- `Entity`: 验证失败的实体名称
- `ErrorMessage`: 错误信息

## 最佳实践
1. **实体命名**：使用表达式添加实体时，框架会自动生成有意义的实体名称
   ```csharp
   validator.Add(() => request.User.Address, out var addressRef);
   // 实体名称: "request.User.Address"
   ```
   
2. **错误处理**：在验证过程中捕获异常并转换为验证错误
   ```csharp
   validator.Check(userRef, user => {
       try {
           return (user.IsActive(), "");
       }
       catch (Exception ex) {
           return (false, $"用户状态检查失败: {ex.Message}");
       }
   });
   ```
   
3. **资源清理**：使用`Finally`确保资源释放
   ```csharp
   .Finally(result => {
       _dbContext.Dispose();
       _openTelemetry.EndValidationSpan(result);
   })
   ```

4. **性能优化**：对于IO密集型验证，优先使用异步方法
   ```csharp
   validator.CheckAsync(userRef, async user => 
       (await _creditService.CheckCreditLimit(user.Id)), 
       "信用额度不足"
   );
   ```

## 贡献指南
欢迎提交Issue和Pull Request！请确保：
1. 所有新功能都包含单元测试
2. 代码符合现有风格
3. 提交信息清晰描述变更内容

## 许可证
MIT License
