import os
import sys
import subprocess
import logging
import argparse

# Setup Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - [%(levelname)s] - %(message)s',
    handlers=[logging.StreamHandler(sys.stdout)]
)

def run_script(script_name, start_progress, end_progress, ticker=None, extra_args=None):
    """
    Runs a python script and reports progress based on the range provided.
    """
    script_path = os.path.join(os.path.dirname(__file__), script_name)
    if not os.path.exists(script_path):
        logging.error(f"Script not found: {script_path}")
        return False

    logging.info(f"Starting {script_name}...")
    print(f"PROGRESS:{start_progress}")
    sys.stdout.flush()

    try:
        cmd = [sys.executable, script_path]
        if ticker:
            cmd.extend(["--ticker", ticker])
        if extra_args:
            cmd.extend(extra_args)
            
        # Use Popen to stream output line-by-line to avoid deadlocks and buffer issues
        # Redirect stderr to stdout so we only have to drain one pipe.
        process = subprocess.Popen(
            cmd, 
            stdout=subprocess.PIPE, 
            stderr=subprocess.STDOUT, 
            text=True,
            bufsize=1
        )

        # Monitor stdout (which now includes stderr)
        if process.stdout:
            for line in process.stdout:
                line = line.strip()
                if line:
                    # Logs from sub-scripts are forwarded to our own stdout 
                    # which is then captured by the C# service.
                    print(f"[{script_name}] {line}")
                    sys.stdout.flush()

        # Wait for completion
        process.wait()
        
        if process.returncode != 0:
            logging.error(f"Error running {script_name}. See logs above for details.")
            return False
            
        logging.info(f"Finished {script_name}.")
        print(f"PROGRESS:{end_progress}")
        sys.stdout.flush()
        return True
    except Exception as e:
        logging.error(f"Execution failed for {script_name}: {e}")
        return False

def main():
    parser = argparse.ArgumentParser(description="Data Update Pipeline")
    parser.add_argument("--ticker", "-t", type=str, help="Specific ticker to update.")
    parser.add_argument("--force-metadata", action="store_true", help="Force refresh of ticker metadata.")
    parser.add_argument("--skip-daily", action="store_true", help="Skip daily data update subscript entirely.")
    parser.add_argument("--delay", type=float, default=None, help="Delay seconds between ticker updates.")
    parser.add_argument("--start-period", type=int, default=5, help="Lookback period in years if no existing data.")
    parser.add_argument("--full-history", action="store_true", help="Download max period if no existing data.")
    parser.add_argument("--force-period", action="store_true", help="Force download anew within lookback period range without incremental sync.")
    args = parser.parse_args()

    print("PROGRESS:0")
    sys.stdout.flush()

    # Step 1: Update Metadata (0% -> 10%) - Only if forced
    if args.force_metadata:
        if not run_script("update_metadata.py", 5, 10, args.ticker):
            logging.error("Pipeline failed at update_metadata.py")
            sys.exit(1)

    # Step 2: Update Daily Data (10% -> 60%)
    if not args.skip_daily:
        daily_extra_args = []
        if args.delay is not None:
            daily_extra_args.extend(["--delay", str(args.delay)])
        if args.start_period:
            daily_extra_args.extend(["--start-period", str(args.start_period)])
        if args.full_history:
            daily_extra_args.append("--full-history")
        if args.force_period:
            daily_extra_args.append("--force-period")

        if not run_script("update_daily.py", 15, 60, args.ticker, daily_extra_args):
            logging.error("Pipeline failed at update_daily.py")
            sys.exit(1)
    else:
        logging.info("Skipping update_daily.py as requested by --skip-daily")

    # Step 3: Generate Timeframes & Indicators (60% -> 100%)
    if not run_script("generate_timeframes.py", 65, 100, args.ticker):
        logging.error("Pipeline failed at generate_timeframes.py")
        sys.exit(1)

    logging.info("Pipeline completed successfully.")
    print("PROGRESS:100")
    sys.stdout.flush()

if __name__ == "__main__":
    main()
