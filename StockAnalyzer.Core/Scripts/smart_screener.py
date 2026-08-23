import json
import pandas as pd
import pandas_ta as ta

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
