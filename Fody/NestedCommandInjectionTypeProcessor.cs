﻿using System;
using System.Linq;
using JetBrains.Annotations;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Commander.Fody
{
    public class NestedCommandInjectionTypeProcessor : TypeProcessorBase
    {
        private const TypeAttributes DefaultTypeAttributesForCommand = TypeAttributes.SpecialName | TypeAttributes.NestedPrivate | TypeAttributes.BeforeFieldInit;
        private const MethodAttributes ConstructorDefaultMethodAttributes = 
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        private const string OwnerFieldName = "_owner";

        private readonly CommandData _command;
        private readonly MethodDefinition _initializeMethod;

        public NestedCommandInjectionTypeProcessor([NotNull] CommandData command,
            [NotNull] MethodDefinition initializeMethod, TypeDefinition type, ModuleWeaver moduleWeaver) : base(type, moduleWeaver)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            if (initializeMethod == null)
            {
                throw new ArgumentNullException("initializeMethod");
            }
            _command = command;
            _initializeMethod = initializeMethod;
        }

        public CommandData Command
        {
            get { return _command; }
        }

        public MethodDefinition InitializeMethod
        {
            get { return _initializeMethod; }
        }

        public override void Execute()
        {
            var constructor = InjectNestedCommandClass();
            var constructorRef = constructor.Resolve();
            AddCommandPropertyInitialization(constructorRef);
        }

        public MethodDefinition InjectNestedCommandClass()
        {
            var name = string.Format("<>__NestedCommandImplementationFor" + Command.CommandName);
            var commandType = new TypeDefinition(Type.Namespace, name, DefaultTypeAttributesForCommand)
            {
                BaseType = Assets.TypeReferences.Object
            };

            var field = commandType.AddField(Type, OwnerFieldName);
            field.IsInitOnly = true;

            ImplementICommandInterface(commandType);

            var ctor = CreateConstructor(commandType);
            commandType.Methods.Add(ctor);
            Type.NestedTypes.Add(commandType);
            return ctor;
        }

        public void AddCommandPropertyInitialization(MethodReference commandConstructor)
        {
            var method = InitializeMethod;
            if (!method.Body.Variables.Any(vDef => vDef.VariableType.IsBoolean() && vDef.Name == "isNull"))
            {
                var vDef = new VariableDefinition("isNull", Type.Module.TypeSystem.Boolean);
                method.Body.Variables.Add(vDef);
            }

            // var returnInst = CommandInjectionTypeProcessor.GetOrCreateLastReturnInstruction(method);
            var instructions = method.Body.Instructions;
            Instruction blockEnd = Instruction.Create(OpCodes.Nop);

            // Null check
            // if (Command == null) { ... }
            instructions.Prepend(
                Instruction.Create(OpCodes.Nop),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Call, Command.CommandProperty.GetMethod),
                Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Ceq),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Ceq),
                Instruction.Create(OpCodes.Stloc_0),
                Instruction.Create(OpCodes.Ldloc_0),
                Instruction.Create(OpCodes.Brtrue_S, blockEnd),
                blockEnd
                );

            instructions.BeforeInstruction(inst => inst == blockEnd,
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Newobj, commandConstructor),
                Instruction.Create(OpCodes.Call, Command.CommandProperty.SetMethod),
                Instruction.Create(OpCodes.Nop),
                Instruction.Create(OpCodes.Nop)
                );

            Command.CommandInitializationInjected = true;
            Command.UsesNestedCommand = true;
        }

        internal MethodDefinition CreateConstructor(TypeDefinition type)
        {
            var ctor = new MethodDefinition(".ctor", ConstructorDefaultMethodAttributes, Assets.TypeReferences.Void);
            var parameter = new ParameterDefinition("owner", ParameterAttributes.None, Type);
            var field = type.Fields[0];

            ctor.Parameters.Add(parameter);
            var il = ctor.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Call, Assets.ObjectConstructorReference));
            il.Append(Instruction.Create(OpCodes.Nop));
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Stfld, field));
            il.Append(Instruction.Create(OpCodes.Nop));
            il.Append(Instruction.Create(OpCodes.Ret));
            return ctor;
        }

        internal void ImplementICommandInterface(TypeDefinition commandType)
        {
            commandType.Interfaces.Add(Assets.TypeReferences.ICommand);
            Assets.AddCanExecuteChangedEvent(commandType);
            AddCanExecuteMethod(commandType);
            AddExecuteMethod(commandType);
        }           

        internal void AddExecuteMethod(TypeDefinition commandType)
        {
            var field = commandType.Fields[0];
            var method = new MethodDefinition("Execute",
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot |
                MethodAttributes.Virtual, Assets.TypeReferences.Void)
            {
                Body = {InitLocals = true}
            };

            var commandParameter = new ParameterDefinition("parameter", ParameterAttributes.None, Assets.TypeReferences.Object);
            method.Parameters.Add(commandParameter);

            commandType.Methods.Add(method);

            var il = method.Body.GetILProcessor();
            var start = Instruction.Create(OpCodes.Nop);
            il.Append(start);
            foreach (var onExecuteMethod in Command.OnExecuteMethods)
            {
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldfld, field));   
                if (onExecuteMethod.Parameters.Count == 1)
                {
                    il.Append(Instruction.Create(OpCodes.Ldarg_1));
                    var parameter = onExecuteMethod.Parameters[0];
                    if (!parameter.ParameterType.FullNameMatches(Assets.TypeReferences.Object))
                    {
                        if (parameter.ParameterType.IsGenericInstance)
                        {

                        }
                        else
                        {
                            il.Append(Instruction.Create(OpCodes.Unbox_Any, parameter.ParameterType));
                        }
                    }
                }                
                if (onExecuteMethod.IsVirtual)
                {
                    il.Append(Instruction.Create(OpCodes.Callvirt, onExecuteMethod));
                }
                else
                {
                    il.Append(Instruction.Create(OpCodes.Call, onExecuteMethod));
                }
            }    
            il.Append(Instruction.Create(OpCodes.Nop));     
            il.Append(Instruction.Create(OpCodes.Ret));
        }

        internal void AddCanExecuteMethod(TypeDefinition commandType)
        {
            var field = commandType.Fields[0];

            var method = new MethodDefinition("CanExecute",
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot |
                MethodAttributes.Virtual, Assets.TypeReferences.Boolean)
            {
                Body = {InitLocals = true}
            };

            var commandParameter = new ParameterDefinition("parameter", ParameterAttributes.None, Assets.TypeReferences.Object);
            method.Parameters.Add(commandParameter);

            var returnVariable = new VariableDefinition(Assets.TypeReferences.Boolean);
            method.Body.Variables.Add(returnVariable);

            commandType.Methods.Add(method);

            var il = method.Body.GetILProcessor();            
            il.Append(Instruction.Create(OpCodes.Nop));
            if (Command.CanExecuteMethods.Count == 0)
            {
                var returnBlock = Instruction.Create(OpCodes.Ldloc_0);
                il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
                il.Append(Instruction.Create(OpCodes.Stloc_0));
                il.Append(Instruction.Create(OpCodes.Br_S, returnBlock));                
                il.Append(returnBlock);
                il.Append(Instruction.Create(OpCodes.Ret));
            }
            else
            {
                var canExecuteMethod = Command.CanExecuteMethods.Single();
                var returnBlock = Instruction.Create(OpCodes.Nop);
                il.Append(Instruction.Create(OpCodes.Ldarg_0));
                il.Append(Instruction.Create(OpCodes.Ldfld, field));
                if (canExecuteMethod.IsVirtual)
                {
                    il.Append(Instruction.Create(OpCodes.Callvirt, canExecuteMethod));
                }
                else
                {
                    il.Append(Instruction.Create(OpCodes.Call, canExecuteMethod));
                }
                il.Append(Instruction.Create(OpCodes.Stloc_0));
                il.Append(Instruction.Create(OpCodes.Br_S, returnBlock));
                il.Append(returnBlock);
                il.Append(Instruction.Create(OpCodes.Ldloc_0));
                il.Append(Instruction.Create(OpCodes.Ret));
            }            
        }
    }
}