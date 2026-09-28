
import importlib
import logging
import pandas as pd
import os

class IndicatorAdapter:
    """
    Dynamically loads and executes indicators from the 'indicators' package.
    """
    
    def __init__(self):
        self.indicators_package = "indicators"

    def calculate(self, indicator_name: str, candles: list, params: dict):
        """
        Loads the module '{indicator_name}.py', instantiates code, and runs calculate.
        
        Args:
            indicator_name (str): The name of the indicator file (e.g., 'SimpleMovingAverage').
            candles (list): List of dicts [{'date':..., 'close':...}, ...].
            params (dict): Dictionary of parameters (e.g., {'period': 20}).
        
        Returns:
            list: The calculated values.
        """
        if not indicator_name:
            raise ValueError("Indicator name is required")

        # 1. Dynamic Import
        try:
            # Assumes file name matches class name usually, but we need to check usage.
            # Based on inspection:
            # File: SimpleMovingAverage.py
            # Class: SimpleMovingAverage
            # Function: calculate(candles, period=14)
            
            module_path = f"{self.indicators_package}.{indicator_name}"
            module = importlib.import_module(module_path)
            
            # 2. Resolve Calculation Method
            # All identified files have a static wrapper `def calculate(candles, ...)`
            if hasattr(module, 'calculate') and callable(module.calculate):
                func = module.calculate
            else:
                raise AttributeError(f"Module {indicator_name} does not have a 'calculate' function.")
            
            # 3. Prepare Arguments
            # Inspect function signature if needed, or assume kwargs work?
            # Most wrappers are: def calculate(candles, period=14): using positional or specific args.
            # We need to map `params` (dict) to the function arguments.
            
            # Simple approach: Check typically used params (period, etc.)
            # Or use inspect to bind arguments
            import inspect
            sig = inspect.signature(func)
            
            bound_args = {}
            for param_name, param in sig.parameters.items():
                if param_name == 'candles':
                    continue
                
                if param_name in params:
                    val = params[param_name]
                    
                    # Robust Type Casting
                    # 1. Try Default Value Inference
                    if param.default is not inspect.Parameter.empty:
                        if isinstance(param.default, int):
                            try: val = int(val)
                            except: pass
                        elif isinstance(param.default, float):
                            try: val = float(val)
                            except: pass
                        elif isinstance(param.default, bool):
                            # Handle 'true'/'false' strings
                            if isinstance(val, str):
                                val = val.lower() == 'true'
                    else:
                        # 2. Heuristic Inference (for args without defaults like 'period')
                        # Try int, then float
                        if isinstance(val, str):
                            if val.isdigit() or (val.startswith('-') and val[1:].isdigit()):
                                try: val = int(val)
                                except: pass
                            else:
                                try: 
                                    v_float = float(val)
                                    val = v_float
                                except: pass

                    bound_args[param_name] = val
                    
            # 4. Execute
            logging.info(f"Calculating {indicator_name} with params: {bound_args}")
            result = func(candles, **bound_args)
            return result

        except ImportError as e:
            logging.error(f"Indicator {indicator_name} not found: {e}")
            raise ValueError(f"Indicator {indicator_name} not found.")
        except Exception as e:
            logging.error(f"Error calculating {indicator_name}: {e}")
            raise e
