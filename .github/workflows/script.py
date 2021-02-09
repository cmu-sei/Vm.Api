import os

header_test = 'Released under a MIT (SEI)-style license'
header = 'Copyright 2021 Carnegie Mellon University. All Rights Reserved. \n Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.'

print('header not in:')
# iterate over all files in directory 
for root, dirs, files in os.walk("."):
 for file in files:
  # only care about files with extensions considered to be source code
  if file.endswith(('.cs', '.ts', '.js', '.css', '.php', '.xml', '.html', '.scss', '.py', '.go')):
   # open file & read it
   with open(os.path.join(root,file), 'r') as original: data = original.read()
   # check file for header
   if (data.find(header_test) == -1):
    print(file)
    # add header to file
    if file.endswith(('.cs', '.ts', '.js', '.css', '.go', '.scss', '.php')):
     # comment type ' /* __ */
     with open(os.path.join(root,file), 'w') as modified: modified.write('/*\n' + header + '\n*/\n\n' + data)    
    elif file.endswith(('.xml', '.html')):
     # comment type ' <!-- __ --> '
     with open(os.path.join(root,file), 'w') as modified: modified.write('<!--\n' + header + '\n-->\n\n' + data)
    elif file.endswith('py'):
     # comment type ' """ ___ """ '
     with open(os.path.join(root,file), 'w') as modified: modified.write('"""\n' + header + '\n"""\n\n' + data)
