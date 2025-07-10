# FlowValidator - Chained Validation Framework     [中文](https://github.com/8720826/FlowValidator/blob/main/README.md)

## Introduction
`FlowValidator` is a fluent validation framework designed for complex business scenarios. It features a chainable API that supports hybrid synchronous/asynchronous validation, entity dependency management, and cross-entity validation. The framework solves common validation challenges like sequential dependencies, mixed I/O operations, and complex business rules.

## Key Features
- **Fluent API**: Chain validation steps in a natural sequence
- **Hybrid Validation**: Mix synchronous and asynchronous operations
- **Entity References**: Safely access entities during validation
- **Cross-Validation**: Validate relationships between multiple entities
- **Process Control**: Success/failure callbacks with entity access
- **Short-Circuiting**: Stop execution on first failure
- **Null Safety**: Automatic null checks for reference types

## Quick Start
```csharp
var validator = new FlowValidator();

validator.Add(() => request.Username, out var usernameRef)
         .CheckNotNullOrEmpty(usernameRef, "Username required")
         .Check(usernameRef, n => n.Length >= 5, "Min 5 characters");

validator.Add(() => request.Email, out var emailRef)
         .Check(emailRef, e => Regex.IsMatch(e, @"^\S+@\S+\.\S+$"), "Invalid email");

var result = await validator.ValidateAsync();
if (!result.IsValid) return BadRequest(result);
```

## Core Concepts

### EntityRef<T>
Safe reference to validation entities:
```csharp
validator.Add(() => request.ProductId, out var productIdRef);
validator.AddAsync(async () => await _db.Products.FindAsync(productIdRef.Value), 
                  out var productRef);
```

### ValidationFlow
Control post-validation behavior:
```csharp
await validator.StartFlow()
    .OnSuccess(entities => CreateOrder(entities))
    .OnFailure((result, entities) => LogFailure(result, entities))
    .Finally(result => CleanupResources())
    .ExecuteAsync();
```

## Usage Examples

### 1. User Registration
```csharp
public async Task Register(UserRegistrationDto dto)
{
    var validator = new FlowValidator();
    
    validator.Add(() => dto.Username, out var usernameRef)
             .CheckNotNullOrEmpty(usernameRef);
    
    validator.Add(() => dto.Password, out var passwordRef)
             .CheckNotNullOrEmpty(passwordRef);
    
    validator.CheckAsync(usernameRef, 
        async name => (await _userService.Exists(name)) ? (false, "Username taken") : (true, null));
    
    validator.ThenAdd(entities => 
        new PasswordScorer().Score(passwordRef.Value), 
        out var scoreRef)
        .Check(scoreRef, s => s >= 8, "Weak password");

    await validator.StartFlow()
        .OnSuccess(entities => _repo.CreateUser(dto))
        .OnFailure((result, _) => _logger.Warn($"Registration failed: {result}"))
        .ExecuteAsync();
}
```

### 2. Order Processing
```csharp
public async Task ProcessOrder(OrderRequest request)
{
    var validator = new FlowValidator();
    
    validator.Add(() => request.OrderId, out var orderIdRef);
    validator.AddAsync(async () => await _orderService.Get(orderIdRef.Value), out var orderRef);
    
    validator.Add(() => request.PaymentMethod, out var paymentRef)
             .Check(paymentRef, p => _acceptedMethods.Contains(p), "Unsupported payment");
    
    validator.CheckCrossAsync(
        orderRef, 
        paymentRef,
        async (order, method) => 
            (await _paymentGateway.Validate(order.Total, method), 
        "Payment validation failed"
    );
    
    await validator.StartFlow()
        .OnSuccessAsync(async entities => {
            await _paymentGateway.Charge(orderRef.Value.Total);
            await _inventoryService.Reserve(orderRef.Value.Items);
        })
        .OnFailureAsync(async (result, entities) => {
            await _notificationService.SendDeclined(orderRef.Value.UserId);
        })
        .ExecuteAsync();
}
```

### 3. Inventory Management
```csharp
public async Task UpdateStock(StockUpdateCommand command)
{
    var validator = new FlowValidator();
    
    validator.Add(() => command.ProductId, out var productIdRef);
    validator.AddAsync(async () => await _productRepo.Get(productIdRef.Value), out var productRef);
    
    validator.Add(() => command.WarehouseId, out var warehouseRef);
    validator.AddAsync(async entities => 
        await _inventoryService.GetStock(productRef.Value.SKU, warehouseRef.Value), 
        out var stockRef);
    
    validator.ThenAdd(entities => 
        _policyFactory.GetPolicy(productRef.Value.Category), 
        out var policyRef);
    
    validator.CheckCross(
        command, 
        stockRef, 
        policyRef,
        (cmd, stock, policy) => policy.CanUpdate(stock, cmd.Adjustment),
        "Update violates policy"
    );
    
    await validator.StartFlow()
        .OnSuccess(entities => {
            stockRef.Value.Adjust(command.Adjustment);
            _inventoryRepo.Update(stockRef.Value);
        })
        .ExecuteAsync();
}
```

### 4. API Validation
```csharp
[HttpPost]
public async Task<IActionResult> CreatePost([FromBody] PostRequest request)
{
    var validator = new FlowValidator();
    
    validator.Add(() => request.Title, out var titleRef)
             .CheckNotNullOrEmpty(titleRef)
             .Check(titleRef, t => t.Length <= 120, "Title too long");
             
    validator.Add(() => request.Content, out var contentRef)
             .CheckNotNullOrEmpty(contentRef)
             .Check(contentRef, c => c.Length <= 10000, "Content too long");
             
    validator.Add(() => request.AuthorId, out var authorRef)
             .Check(authorRef, id => id > 0, "Invalid author");
    
    var result = await validator.ValidateAsync();
    return result.IsValid ? Ok() : BadRequest(result);
}
```

## Best Practices

### Entity Naming
Use expressions for meaningful names:
```csharp
validator.Add(() => request.User.Address.City, out var cityRef);
// Name becomes: "request.User.Address.City"
```

### Error Handling
Convert exceptions to validation errors:
```csharp
validator.Check(orderRef, order => {
    try {
        return (order.Validate(), "");
    }
    catch (Exception ex) {
        return (false, $"Validation error: {ex.Message}");
    }
});
```

### Resource Management
Clean up in finally blocks:
```csharp
.Finally(result => {
    _dbConnection.Dispose();
    _metrics.RecordValidationDuration(result);
})
```

### Performance
Use async for I/O operations:
```csharp
validator.CheckAsync(productRef, async p => 
    (await _searchService.IsIndexed(p.Id), "Product not searchable")
);
```

## Advanced Features

### Custom Validators
```csharp
public static class CustomValidators
{
    public static FlowValidator CheckEmailFormat(
        this FlowValidator validator, 
        EntityRef<string> emailRef)
    {
        return validator.Check(emailRef, e => 
            Regex.IsMatch(e, @"^\S+@\S+\.\S+$"), 
            "Invalid email format");
    }
}

// Usage:
validator.CheckEmailFormat(emailRef);
```

### Cross-Validation
Validate relationships between entities:
```csharp
validator.CheckCross(
    startDateRef, 
    endDateRef,
    (start, end) => start < end,
    "End date must be after start date"
);
```

## Contribution
Contributions are welcome! Please:
1. Add tests for new features
2. Maintain coding style consistency
3. Document public APIs
4. Update README for significant changes

## License
MIT License
