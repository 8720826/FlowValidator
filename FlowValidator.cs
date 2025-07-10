using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace YesCent
{
    // 基础验证结果类
    public class ValidationResult
    {
        public bool IsValid { get; private set; } = true;
        public string Entity { get; private set; }
        public string ErrorMessage { get; private set; }

        public void SetError(string entity, string errorMessage)
        {
            if (!IsValid) return;
            IsValid = false;
            Entity = entity;
            ErrorMessage = errorMessage;
        }

        public override string ToString() => IsValid ? "验证成功" : $"[{Entity}]: {ErrorMessage}";
    }

    // 实体引用
    public class EntityRef<T>
    {
        public string Name { get; }

        public EntityRef(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

        public T GetEntity(ConcurrentDictionary<string, object> entities)
        {
            if (entities.TryGetValue(Name, out var obj) && obj is T entity)
                return entity;

            throw new KeyNotFoundException($"实体 '{Name}' 不存在");
        }
    }

    // 验证异常
    public class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }
    }

    // 增强的验证流程构建器（失败回调也能访问实体）
    public class ValidationFlow
    {
        private readonly FlowValidator _validator;
        private Func<ConcurrentDictionary<string, object>, Task> _onSuccess;
        private Func<ValidationResult, ConcurrentDictionary<string, object>, Task> _onFailure; // 修改为接收两个参数
        private Func<ValidationResult, Task> _finally;

        public ValidationFlow(FlowValidator validator)
        {
            _validator = validator;
        }

        // 同步成功回调
        public ValidationFlow OnSuccess(Action<ConcurrentDictionary<string, object>> action)
        {
            _onSuccess = dict => { action(dict); return Task.CompletedTask; };
            return this;
        }

        // 异步成功回调
        public ValidationFlow OnSuccessAsync(Func<ConcurrentDictionary<string, object>, Task> asyncAction)
        {
            _onSuccess = asyncAction;
            return this;
        }

        // 同步失败回调（现在可以访问实体集合）
        public ValidationFlow OnFailure(Action<ValidationResult, ConcurrentDictionary<string, object>> action)
        {
            _onFailure = (result, entities) => { action(result, entities); return Task.CompletedTask; };
            return this;
        }

        // 异步失败回调（现在可以访问实体集合）
        public ValidationFlow OnFailureAsync(Func<ValidationResult, ConcurrentDictionary<string, object>, Task> asyncAction)
        {
            _onFailure = asyncAction;
            return this;
        }

        // 同步最终处理
        public ValidationFlow Finally(Action<ValidationResult> action)
        {
            _finally = result => { action(result); return Task.CompletedTask; };
            return this;
        }

        // 异步最终处理
        public ValidationFlow FinallyAsync(Func<ValidationResult, Task> asyncAction)
        {
            _finally = asyncAction;
            return this;
        }

        public async Task<ValidationResult> ExecuteAsync()
        {
            var result = await _validator.ValidateAsync();
            var entities = _validator.GetEntities(); // 获取实体集合

            try
            {
                if (result.IsValid && _onSuccess != null)
                {
                    await _onSuccess(entities);
                }
                else if (!result.IsValid && _onFailure != null)
                {
                    // 传递验证结果和实体集合
                    await _onFailure(result, entities);
                }
            }
            finally
            {
                if (_finally != null)
                {
                    await _finally(result);
                }
            }

            return result;
        }
    }

    // 核心验证器（支持链式调用）
    public class FlowValidator
    {
        private readonly ConcurrentDictionary<string, object> _entities = new();
        private readonly ValidationResult _result = new();
        private readonly List<Func<Task>> _tasks = new();
        private bool _executed;

        public bool HasError => !_result.IsValid;

        // 获取实体字典
        public ConcurrentDictionary<string, object> GetEntities() => _entities;

        // 启动验证流程
        public ValidationFlow StartFlow() => new ValidationFlow(this);

        // 添加同步实体（链式调用）
        public FlowValidator Add<T>(Expression<Func<T>> entityExpr, out EntityRef<T> entityRef)
        {
            if (_executed) throw new ValidationException("验证器已执行");
            if (HasError)
            {
                entityRef = new EntityRef<T>("");
                return this;
            }

            var name = GetExpressionName(entityExpr);
            entityRef = new EntityRef<T>(name);

            _tasks.Add(() =>
            {
                if (HasError) return Task.CompletedTask;

                try
                {
                    var entity = entityExpr.Compile().Invoke();

                    // 仅对引用类型和可空值类型进行null检查
                    if (entity == null && default(T) == null)
                    {
                        _result.SetError(name, "实体不能为null");
                        return Task.CompletedTask;
                    }

                    _entities[name] = entity;
                }
                catch (Exception ex)
                {
                    _result.SetError(name, $"添加实体失败: {ex.Message}");
                }

                return Task.CompletedTask;
            });

            return this;
        }

        // 添加异步实体（链式调用）
        public FlowValidator AddAsync<T>(Func<Task<T>> asyncEntityFunc, out EntityRef<T> entityRef)
        {
            if (_executed) throw new ValidationException("验证器已执行");
            if (HasError)
            {
                entityRef = new EntityRef<T>("");
                return this;
            }

            var name = $"Async_{Guid.NewGuid():N}";
            entityRef = new EntityRef<T>(name);

            _tasks.Add(async () =>
            {
                if (HasError) return;

                try
                {
                    var entity = await asyncEntityFunc();

                    if (entity == null && default(T) == null)
                    {
                        _result.SetError(name, "实体不能为null");
                        return;
                    }

                    _entities[name] = entity;
                }
                catch (Exception ex)
                {
                    _result.SetError(name, $"获取实体失败: {ex.Message}");
                }
            });

            return this;
        }

        // 添加依赖前面实体的同步实体（链式调用）
        public FlowValidator ThenAdd<T>(Func<ConcurrentDictionary<string, object>, T> factory,
                                       out EntityRef<T> entityRef,
                                       string name = null)
        {
            if (_executed) throw new ValidationException("验证器已执行");
            if (HasError)
            {
                entityRef = new EntityRef<T>("");
                return this;
            }

            name = name ?? $"Then_{Guid.NewGuid():N}";
            entityRef = new EntityRef<T>(name);

            _tasks.Add(() =>
            {
                if (HasError) return Task.CompletedTask;

                try
                {
                    var entity = factory(_entities);

                    if (entity == null && default(T) == null)
                    {
                        _result.SetError(name, "实体不能为null");
                        return Task.CompletedTask;
                    }

                    _entities[name] = entity;
                }
                catch (Exception ex)
                {
                    _result.SetError(name, $"创建实体失败: {ex.Message}");
                }

                return Task.CompletedTask;
            });

            return this;
        }

        // 添加依赖前面实体的异步实体（链式调用）
        public FlowValidator ThenAddAsync<T>(Func<ConcurrentDictionary<string, object>, Task<T>> factory,
                                            out EntityRef<T> entityRef,
                                            string name = null)
        {
            if (_executed) throw new ValidationException("验证器已执行");
            if (HasError)
            {
                entityRef = new EntityRef<T>("");
                return this;
            }

            name = name ?? $"ThenAsync_{Guid.NewGuid():N}";
            entityRef = new EntityRef<T>(name);

            _tasks.Add(async () =>
            {
                if (HasError) return;

                try
                {
                    var entity = await factory(_entities);

                    if (entity == null && default(T) == null)
                    {
                        _result.SetError(name, "实体不能为null");
                        return;
                    }

                    _entities[name] = entity;
                }
                catch (Exception ex)
                {
                    _result.SetError(name, $"异步创建实体失败: {ex.Message}");
                }
            });

            return this;
        }

        // 添加同步验证（链式调用）
        public FlowValidator Check<T>(EntityRef<T> entityRef, Func<T, (bool IsValid, string ErrorMessage)> validator)
        {
            if (_executed) throw new ValidationException("验证器已执行");
            if (HasError) return this;

            _tasks.Add(() =>
            {
                if (HasError) return Task.CompletedTask;

                try
                {
                    var entity = entityRef.GetEntity(_entities);
                    var (isValid, error) = validator(entity);

                    if (!isValid)
                    {
                        _result.SetError(entityRef.Name, error);
                    }
                }
                catch (KeyNotFoundException)
                {
                    _result.SetError(entityRef.Name, "实体未解析");
                }
                catch (InvalidCastException)
                {
                    _result.SetError(entityRef.Name, "实体类型错误");
                }
                catch (Exception ex)
                {
                    _result.SetError(entityRef.Name, $"验证错误: {ex.Message}");
                }

                return Task.CompletedTask;
            });

            return this;
        }

        // 添加异步验证（链式调用）
        public FlowValidator CheckAsync<T>(EntityRef<T> entityRef, Func<T, Task<(bool IsValid, string ErrorMessage)>> asyncValidator)
        {
            if (_executed) throw new ValidationException("验证器已执行");
            if (HasError) return this;

            _tasks.Add(async () =>
            {
                if (HasError) return;

                try
                {
                    var entity = entityRef.GetEntity(_entities);
                    var (isValid, error) = await asyncValidator(entity);

                    if (!isValid)
                    {
                        _result.SetError(entityRef.Name, error);
                    }
                }
                catch (KeyNotFoundException)
                {
                    _result.SetError(entityRef.Name, "实体未解析");
                }
                catch (InvalidCastException)
                {
                    _result.SetError(entityRef.Name, "实体类型错误");
                }
                catch (Exception ex)
                {
                    _result.SetError(entityRef.Name, $"异步验证错误: {ex.Message}");
                }
            });

            return this;
        }

        // 直接执行验证
        public async Task<ValidationResult> ValidateAsync()
        {
            if (_executed) return _result;

            foreach (var task in _tasks)
            {
                if (HasError) break;
                await task();
            }

            _executed = true;
            return _result;
        }

        // 表达式解析辅助方法
        private string GetExpressionName<T>(Expression<Func<T>> expr)
        {
            var parts = new List<string>();
            var current = expr.Body;

            while (current != null)
            {
                if (current is MemberExpression member)
                {
                    parts.Add(member.Member.Name);
                    current = member.Expression;
                }
                else if (current is ParameterExpression param)
                {
                    parts.Add(param.Name);
                    break;
                }
                else if (current is ConstantExpression constant)
                {
                    parts.Add(constant.Value?.ToString() ?? "constant");
                    break;
                }
                else
                {
                    break;
                }
            }

            parts.Reverse();
            return string.Join(".", parts);
        }
    }

    // 验证器扩展方法
    public static class ValidatorExtensions
    {
        // 获取实体值的扩展方法
        public static T GetValue<T>(this EntityRef<T> entityRef, FlowValidator validator)
        {
            return entityRef.GetEntity(validator.GetEntities());
        }

        // 获取实体值的扩展方法（带默认值）
        public static T GetValueOrDefault<T>(this EntityRef<T> entityRef,
            FlowValidator validator, T defaultValue = default)
        {
            try
            {
                return entityRef.GetEntity(validator.GetEntities());
            }
            catch
            {
                return defaultValue;
            }
        }

        // 空值检查
        public static FlowValidator CheckNotNull<T>(this FlowValidator validator,
            EntityRef<T> entityRef, string errorMessage = "值不能为null")
        {
            return validator.Check(entityRef, value =>
                (value != null || !IsReferenceOrNullableType(typeof(T)),
                value == null ? errorMessage : ""));
        }

        // 集合非空检查
        public static FlowValidator CheckNotEmpty<T>(this FlowValidator validator,
            EntityRef<IEnumerable<T>> entityRef, string errorMessage = "集合不能为空")
        {
            return validator.Check(entityRef, c =>
                (c != null && c.Any(), errorMessage));
        }

        // 字符串非空检查
        public static FlowValidator CheckNotNullOrEmpty(this FlowValidator validator,
            EntityRef<string> entityRef, string errorMessage = "字符串不能为空")
        {
            return validator.Check(entityRef, s =>
                (!string.IsNullOrEmpty(s), errorMessage));
        }

        // 同步交叉验证（两个实体）
        public static FlowValidator CheckCross<T1, T2>(
            this FlowValidator validator,
            EntityRef<T1> ref1,
            EntityRef<T2> ref2,
            Func<T1, T2, bool> condition,
            string errorMessage)
        {
            return validator.Check(ref1, v1 =>
            {
                try
                {
                    var v2 = ref2.GetValue(validator);
                    return (condition(v1, v2), errorMessage);
                }
                catch (KeyNotFoundException)
                {
                    return (false, "依赖实体未解析");
                }
                catch (InvalidCastException)
                {
                    return (false, "依赖实体类型错误");
                }
            });
        }

        // 同步交叉验证（三个实体）
        public static FlowValidator CheckCross<T1, T2, T3>(
            this FlowValidator validator,
            EntityRef<T1> ref1,
            EntityRef<T2> ref2,
            EntityRef<T3> ref3,
            Func<T1, T2, T3, bool> condition,
            string errorMessage)
        {
            return validator.Check(ref1, v1 =>
            {
                try
                {
                    var v2 = ref2.GetValue(validator);
                    var v3 = ref3.GetValue(validator);
                    return (condition(v1, v2, v3), errorMessage);
                }
                catch (KeyNotFoundException)
                {
                    return (false, "依赖实体未解析");
                }
                catch (InvalidCastException)
                {
                    return (false, "依赖实体类型错误");
                }
            });
        }

        // 异步交叉验证（两个实体）
        public static FlowValidator CheckCrossAsync<T1, T2>(
            this FlowValidator validator,
            EntityRef<T1> ref1,
            EntityRef<T2> ref2,
            Func<T1, T2, Task<bool>> asyncCondition,
            string errorMessage)
        {
            return validator.CheckAsync(ref1, async v1 =>
            {
                try
                {
                    var v2 = ref2.GetValue(validator);
                    var isValid = await asyncCondition(v1, v2);
                    return (isValid, isValid ? "" : errorMessage);
                }
                catch (KeyNotFoundException)
                {
                    return (false, "依赖实体未解析");
                }
                catch (InvalidCastException)
                {
                    return (false, "依赖实体类型错误");
                }
                catch (Exception ex)
                {
                    return (false, $"异步验证错误: {ex.Message}");
                }
            });
        }

        // 异步交叉验证（三个实体）
        public static FlowValidator CheckCrossAsync<T1, T2, T3>(
            this FlowValidator validator,
            EntityRef<T1> ref1,
            EntityRef<T2> ref2,
            EntityRef<T3> ref3,
            Func<T1, T2, T3, Task<bool>> asyncCondition,
            string errorMessage)
        {
            return validator.CheckAsync(ref1, async v1 =>
            {
                try
                {
                    var v2 = ref2.GetValue(validator);
                    var v3 = ref3.GetValue(validator);
                    var isValid = await asyncCondition(v1, v2, v3);
                    return (isValid, isValid ? "" : errorMessage);
                }
                catch (KeyNotFoundException)
                {
                    return (false, "依赖实体未解析");
                }
                catch (InvalidCastException)
                {
                    return (false, "依赖实体类型错误");
                }
                catch (Exception ex)
                {
                    return (false, $"异步验证错误: {ex.Message}");
                }
            });
        }

        // 判断是否为引用类型或可空值类型
        private static bool IsReferenceOrNullableType(Type type)
        {
            return !type.IsValueType ||
                  type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }
    }
}