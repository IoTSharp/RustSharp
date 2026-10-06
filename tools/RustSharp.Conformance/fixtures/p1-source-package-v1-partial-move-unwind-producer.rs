pub struct Resource { pub value: i32 }

impl Drop for Resource {
    fn drop(&mut self) { println!("{}", self.value); }
}

pub fn make() -> (Resource, Resource) {
    (Resource { value: 1 }, Resource { value: 2 })
}

pub fn fail(pair: (Resource, Resource)) -> i32 {
    let first = pair.0;
    let max = 2147483647;
    max + 1
}

fn main() {}
