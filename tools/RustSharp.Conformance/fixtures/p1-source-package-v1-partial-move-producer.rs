pub struct Resource { pub value: i32 }

impl Drop for Resource {
    fn drop(&mut self) { println!("{}", self.value); }
}

pub fn make() -> (Resource, Resource) {
    (Resource { value: 1 }, Resource { value: 2 })
}

pub fn project(pair: (Resource, Resource)) -> Resource { pair.0 }

fn main() {}
