import ast
import json
import pandas as pd

_SAFE_AST_NODES = (
    ast.Expression,
    ast.BoolOp, ast.And, ast.Or,
    ast.BinOp, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Mod, ast.BitAnd, ast.BitOr,
    ast.UnaryOp, ast.Not, ast.USub, ast.UAdd,
    ast.Compare, ast.Eq, ast.NotEq, ast.Lt, ast.LtE, ast.Gt, ast.GtE, ast.In, ast.NotIn, ast.Is, ast.IsNot,
    ast.Name, ast.Constant, ast.List, ast.Tuple, ast.Load,
)

def _is_safe_query_expr(criteria: str) -> bool:
    if not isinstance(criteria, str):
        return False
    stripped = criteria.strip()
    if not stripped or len(stripped) > 512 or "@" in stripped:
        return False
    try:
        tree = ast.parse(stripped, mode="eval")
    except SyntaxError:
        return False
    for node in ast.walk(tree):
        if not isinstance(node, _SAFE_AST_NODES):
            return False
        if isinstance(node, ast.Name) and node.id.startswith("_"):
            return False
        if isinstance(node, ast.Constant):
            if not isinstance(node.value, (int, float, str, bool, type(None))):
                return False
            if isinstance(node.value, str) and len(node.value) > 64:
                return False
            if isinstance(node.value, (int, float)) and abs(node.value) > 1e15:
                return False
        if isinstance(node, ast.BinOp):
            for child in (node.left, node.right):
                if isinstance(child, (ast.List, ast.Tuple)):
                    return False
                if isinstance(child, ast.Constant) and isinstance(child.value, str):
                    return False
        if isinstance(node, ast.UnaryOp):
            if isinstance(node.operand, (ast.List, ast.Tuple)):
                return False
            if isinstance(node.operand, ast.Constant) and isinstance(node.operand.value, str):
                return False
    return True

def screen_stocks(json_file_path, criteria):
    """
    Screens stocks based on a query string.
    
    Args:
        json_file_path (str): Path to the latest_screening_data.json file.
        criteria (str): Pandas query string (e.g. 'RSI_14 < 30 and Close > 100').
        
    Returns:
        list: List of matching symbols.
    """
    try:
        if not _is_safe_query_expr(criteria):
            return []

        with open(json_file_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
            
        if not data or 'data' not in data:
            return []
            
        # Convert list of dicts to DataFrame
        # The JSON structure has nested 'indicators', we need to flatten it
        rows = []
        for item in data['data']:
            row = item.copy()
            indicators = row.pop('indicators', {})
            
            # Flatten indicators
            for k, v in indicators.items():
                if isinstance(v, list):
                    # For list indicators (like Bollinger Bands), likely we want specific values
                    # But for simple querying, maybe we just store them as is or specific indices?
                    # Let's try to handle common ones or just store as is for now.
                    # Actually, query on list is hard. 
                    # If it's a list with 3 values like BB, usually [Upper, Middle, Lower]
                    # Let's expand them if we know the names, or just skip complex ones for simple query
                    # For now, let's keep it simple: 
                    # If it's a list of numbers, maybe average? OR just don't flatten complex ones easily.
                    # Better aproach: Just flatten everything. If v is list, add suffix _0, _1 etc.
                    for i, val in enumerate(v):
                        row[f"{k}_{i}"] = val
                elif isinstance(v, dict):
                     for sub_k, sub_v in v.items():
                         row[f"{k}_{sub_k}"] = sub_v
                else:
                    row[k] = v
                    
            rows.append(row)
            
        df = pd.DataFrame(rows)
        
        # Apply the filter
        # Ensure column names in criteria match DataFrame columns
        # Case sensitivity might be an issue, but let's assume user uses correct casing from JSON keys
        filtered_df = df.query(criteria)
        
        return filtered_df['symbol'].tolist()
        
    except Exception as e:
        print(f"Error in screen_stocks: {e}")
        return []
