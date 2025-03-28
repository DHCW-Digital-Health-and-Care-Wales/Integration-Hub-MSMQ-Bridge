# Define the queue path
$queuePath = ".\private$\WASPQueue1"

# Check if the queue exists, and create it if it doesn't
if (-not [System.Messaging.MessageQueue]::Exists($queuePath)) {
    [System.Messaging.MessageQueue]::Create($queuePath)
    Write-Host "Queue created successfully."
} else {
    Write-Host "Queue already exists."
}

# Path to the XML file
$xmlFilePath = "C:\Users\Mu313340\Desktop\Bin\ADT_A05.xml"

# Read the XML file content
if (Test-Path $xmlFilePath) {
    $xmlContent = Get-Content -Path $xmlFilePath -Raw
} else {
    Write-Host "XML file not found at the specified path."
    exit
}

# Create the message and set its label and body to the XML content
$message = New-Object System.Messaging.Message
$message.Label = "XML Message from File"
$message.Body = $xmlContent  # Set the XML content from the file as the message body

# Create the queue object and send the message
$queue = New-Object System.Messaging.MessageQueue $queuePath
$queue.Send($message)

# Get the message count in the queue
$messageCount = $queue.GetMessageEnumerator2().Count
Write-Host "Message count: $messageCount"
